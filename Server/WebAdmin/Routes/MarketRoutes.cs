using System.Globalization;
using Server.MirDatabase;
using Server.MirEnvir;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// Market / auctions (B5) over <c>Envir.Main.Auctions</c> (mirrors Server.MirForms Account\Market.cs, fixing its quirks:
    /// outstanding bids are refunded and the seller can get the item back).
    /// <code>
    /// GET  /api/admin/market?q=&amp;by=any|item|seller&amp;status=active|expired|sold|all&amp;sort=&amp;dir=&amp;page=&amp;pageSize=
    /// POST /api/admin/market/{auctionId}/expire {reason?}            (Expired = true → the normal collection flow returns it)
    /// POST /api/admin/market/{auctionId}/remove {reason, returnItem}  (remove from Envir.Auctions + seller's AccountInfo.Auctions)
    /// </code>
    /// Listings last <see cref="Globals.ConsignmentLength"/> days (Envir.ProcessAuction).
    /// </summary>
    public sealed class MarketRoutes : IWebAdminRoutes
    {
        public const int MaxReasonLength = 200;

        public void Register(WebAdminRouter router)
        {
            router.Get("/api/admin/market", ListMarket);
            router.Post("/api/admin/market/{auctionId}/expire", ExpireListing);
            router.Post("/api/admin/market/{auctionId}/remove", RemoveListing);
        }

        // ------------------------------------------------------------------ list

        private static async Task ListMarket(WebAdminRequest req)
        {
            var rows = await WebAdminGame.RunOrInlineAsync(() =>
            {
                var envir = Envir.Main;
                var list = new List<MarketRow>(envir.Auctions.Count);

                foreach (var auction in envir.Auctions)
                {
                    if (auction == null) continue;
                    list.Add(BuildRow(auction));
                }

                return list;
            }).ConfigureAwait(false);

            var counts = MarketQuery.Counts(rows);
            var filtered = MarketQuery.Apply(rows, req.Query["q"], req.Query["by"], req.Query["status"], req.Query["sort"], req.Query["dir"]);
            var paging = GuildMarketPaging.Compute(filtered.Count, req.QueryInt("page", 1), req.QueryInt("pageSize", 50));

            await req.Json(new
            {
                rows = filtered.Skip(paging.Skip).Take(paging.PageSize).ToList(),
                total = filtered.Count,
                page = paging.Page,
                pages = paging.Pages,
                pageSize = paging.PageSize,
                counts,
                listingDays = (int)Globals.ConsignmentLength,
                commission = Globals.Commission,
                world = WebAdminGame.WorldState,
            }).ConfigureAwait(false);
        }

        private static MarketRow BuildRow(AuctionInfo a)
        {
            var item = a.Item;
            var info = item?.Info;

            return new MarketRow
            {
                AuctionId = a.AuctionID,
                ItemName = info != null ? info.FriendlyName : (item != null ? "#" + item.ItemIndex : "?"),
                ItemIndex = item?.ItemIndex ?? 0,
                UniqueId = item?.UniqueID ?? 0,
                Count = item?.Count ?? 0,
                CurrentDura = item?.CurrentDura ?? 0,
                MaxDura = item?.MaxDura ?? 0,
                Price = a.Price,
                CurrentBid = a.CurrentBid,
                Seller = a.SellerInfo?.Name,
                SellerIndex = a.SellerIndex,
                SellerAccount = a.SellerInfo?.AccountInfo?.AccountID,
                SellerAccountIndex = a.SellerInfo?.AccountInfo?.Index ?? 0,
                SellerOnline = a.SellerInfo?.Player != null,
                Buyer = a.CurrentBuyerInfo?.Name,
                BuyerIndex = a.CurrentBuyerIndex,
                ConsignmentDate = a.ConsignmentDate,
                Expiry = MarketQuery.ExpiryOf(a.ConsignmentDate),
                ItemType = (int)a.ItemType,
                ItemTypeName = a.ItemType.ToString(),
                Sold = a.Sold,
                Expired = a.Expired,
                Status = MarketQuery.StatusOf(a.Sold, a.Expired),
            };
        }

        // ------------------------------------------------------------------ mutations (game thread)

        private static ulong ParseAuctionId(WebAdminRequest req)
        {
            var raw = req.Route["auctionId"];
            if (raw == null || !ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
                throw new WebAdminException(400, "Invalid auction id");
            return id;
        }

        private static AuctionInfo FindAuction(ulong id)
        {
            foreach (var auction in Envir.Main.Auctions)
            {
                if (auction != null && auction.AuctionID == id) return auction;
            }

            throw new WebAdminException(404, "Listing #" + id + " not found (already collected or removed?)");
        }

        private static string ReadReason(System.Text.Json.JsonElement body)
        {
            var reason = GuildQuery.CleanMessage(body.GetString("reason"));
            if (reason.Length > MaxReasonLength) throw new WebAdminException(422, "Reason is limited to " + MaxReasonLength + " characters");
            return reason;
        }

        private static async Task ExpireListing(WebAdminRequest req)
        {
            var id = ParseAuctionId(req);
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var reason = ReadReason(body);

            var result = await AccountRoutes.AuditIfTimedOutAsync(req, "market.expire", "#" + id, new { id, reason }, () => WebAdminGame.RunAsync(() =>
            {
                var auction = FindAuction(id);
                if (auction.Sold) throw new WebAdminException(409, "Listing #" + id + " is already sold");
                if (auction.Expired) throw new WebAdminException(409, "Listing #" + id + " is already expired");

                auction.Expired = true;

                var itemName = auction.Item?.FriendlyName ?? "item";
                var seller = auction.SellerInfo;
                var notified = false;

                if (seller?.Player != null)
                {
                    var text = "Your market listing '" + itemName + "' was ended by an administrator; collect it at the market."
                               + (reason.Length > 0 ? " Reason: " + reason : string.Empty);
                    seller.Player.ReceiveChat(text, ChatType.Hint);
                    notified = true;
                }

                Envir.Main.RequestSaveNow();

                return new
                {
                    auctionId = id,
                    item = itemName,
                    seller = seller?.Name,
                    bidder = auction.CurrentBuyerInfo?.Name,
                    currentBid = auction.CurrentBid,
                    notified,
                };
            })).ConfigureAwait(false);

            WebAdminAudit.Record(req, "market.expire", "#" + id + " " + result.item, new { result.seller, result.bidder, result.currentBid, result.notified }, reason.Length > 0 ? reason : null);
            await req.Ok(result, "Listing #" + id + " (" + result.item + ") expired; " + (result.seller ?? "the seller") + " can collect it at the market.").ConfigureAwait(false);
        }

        private static async Task RemoveListing(WebAdminRequest req)
        {
            var id = ParseAuctionId(req);
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var reason = ReadReason(body);
            var returnItem = body.GetBool("returnItem", true);

            var result = await AccountRoutes.AuditIfTimedOutAsync(req, "market.remove", "#" + id, new { id, returnItem, reason }, () => WebAdminGame.RunAsync(() =>
            {
                var auction = FindAuction(id);
                var envir = Envir.Main;
                var seller = auction.SellerInfo;
                var itemName = auction.Item?.FriendlyName ?? "item";

                if (returnItem && seller == null)
                    throw new WebAdminException(409, "The seller of listing #" + id + " no longer exists; remove it without returning the item");

                var plan = MarketQuery.PlanRemoval(auction.ItemType, auction.Sold, auction.Price, auction.CurrentBid, auction.CurrentBuyerInfo != null, returnItem);

                var reasonText = reason.Length > 0 ? reason : "No reason given.";

                if (plan.RefundBid > 0 && auction.CurrentBuyerInfo != null)
                {
                    envir.MailCharacter(auction.CurrentBuyerInfo, gold: plan.RefundBid,
                        customMessage: "The market listing '" + itemName + "' was removed by an administrator. Your bid of "
                                       + plan.RefundBid.ToString("N0") + " gold has been refunded.\r\nReason: " + reasonText);
                }

                if (plan.ReturnItem && auction.Item != null)
                {
                    envir.MailCharacter(seller, item: auction.Item,
                        customMessage: "Your market listing '" + itemName + "' was removed by an administrator and the item is returned.\r\nReason: " + reasonText);
                }

                if (plan.PayoutGold > 0)
                {
                    envir.MailCharacter(seller, gold: plan.PayoutGold,
                        customMessage: "Your sold market listing '" + itemName + "' was closed by an administrator. Your earnings of "
                                       + plan.PayoutGold.ToString("N0") + " gold (after commission) are enclosed.\r\nReason: " + reasonText);
                }

                envir.Auctions.Remove(auction);
                seller?.AccountInfo?.Auctions?.Remove(auction);

                var notified = false;
                if (seller?.Player != null && reason.Length > 0)
                {
                    seller.Player.ReceiveChat("Your market listing '" + itemName + "' was removed by an administrator: " + reason, ChatType.Announcement);
                    notified = true;
                }

                envir.RequestSaveNow();

                return new
                {
                    auctionId = id,
                    item = itemName,
                    uniqueId = auction.Item?.UniqueID ?? 0,
                    seller = seller?.Name,
                    sold = auction.Sold,
                    itemReturned = plan.ReturnItem,
                    payoutGold = plan.PayoutGold,
                    refundedBidder = plan.RefundBid > 0 ? auction.CurrentBuyerInfo?.Name : null,
                    refundBid = plan.RefundBid,
                    notified,
                };
            })).ConfigureAwait(false);

            WebAdminAudit.Record(req, "market.remove", "#" + id + " " + result.item, new
            {
                result.seller,
                result.uniqueId,
                result.sold,
                returnItem,
                result.itemReturned,
                result.payoutGold,
                result.refundedBidder,
                result.refundBid,
                result.notified,
            }, reason.Length > 0 ? reason : null);

            var message = "Listing #" + id + " (" + result.item + ") removed";
            if (result.itemReturned) message += "; the item was mailed back to " + (result.seller ?? "the seller");
            else if (result.payoutGold > 0) message += "; " + result.payoutGold.ToString("N0") + " gold earnings mailed to " + (result.seller ?? "the seller");
            else message += "; nothing was returned";
            if (result.refundBid > 0) message += "; " + result.refundedBidder + "'s bid was refunded";
            message += ".";

            await req.Ok(result, message).ConfigureAwait(false);
        }
    }

    /// <summary>Row of GET /api/admin/market.</summary>
    public sealed class MarketRow
    {
        public ulong AuctionId { get; set; }
        public string ItemName { get; set; }
        public int ItemIndex { get; set; }
        public ulong UniqueId { get; set; }
        public int Count { get; set; }
        public int CurrentDura { get; set; }
        public int MaxDura { get; set; }
        public uint Price { get; set; }
        public uint CurrentBid { get; set; }
        public string Seller { get; set; }
        public int SellerIndex { get; set; }
        public string SellerAccount { get; set; }
        public int SellerAccountIndex { get; set; }
        public bool SellerOnline { get; set; }
        public string Buyer { get; set; }
        public int BuyerIndex { get; set; }
        public DateTime ConsignmentDate { get; set; }
        public DateTime Expiry { get; set; }

        /// <summary>(int)MarketItemType: 0 Consign, 1 Auction, 2 GameShop.</summary>
        public int ItemType { get; set; }

        public string ItemTypeName { get; set; }
        public bool Sold { get; set; }
        public bool Expired { get; set; }

        /// <summary>"active" | "expired" | "sold".</summary>
        public string Status { get; set; }
    }

    /// <summary>What removing a listing gives back (pure; see <see cref="MarketQuery.PlanRemoval"/>).</summary>
    public readonly struct MarketRemovalPlan
    {
        public MarketRemovalPlan(bool returnItem, uint payoutGold, uint refundBid)
        {
            ReturnItem = returnItem;
            PayoutGold = payoutGold;
            RefundBid = refundBid;
        }

        /// <summary>Mail the item back to the seller.</summary>
        public bool ReturnItem { get; }

        /// <summary>Gold (after commission) mailed to the seller of an already-sold listing.</summary>
        public uint PayoutGold { get; }

        /// <summary>Gold refunded to the current highest bidder of an unsold auction.</summary>
        public uint RefundBid { get; }
    }

    /// <summary>Pure market helpers (status, expiry, filters, sorting, removal plan), unit-tested.</summary>
    public static class MarketQuery
    {
        public static readonly string[] SortKeys = { "id", "item", "count", "price", "bid", "seller", "date", "expiry", "type", "status" };

        public static string StatusOf(bool sold, bool expired)
        {
            if (sold) return "sold";
            return expired ? "expired" : "active";
        }

        /// <summary>Envir.ProcessAuction ends a listing at ConsignmentDate + Globals.ConsignmentLength days.</summary>
        public static DateTime ExpiryOf(DateTime consignmentDate)
        {
            if (consignmentDate > DateTime.MaxValue.AddDays(-Globals.ConsignmentLength)) return DateTime.MaxValue;
            return consignmentDate.AddDays(Globals.ConsignmentLength);
        }

        public static object Counts(IEnumerable<MarketRow> rows)
        {
            int active = 0, expired = 0, sold = 0, all = 0;
            foreach (var r in rows ?? Enumerable.Empty<MarketRow>())
            {
                if (r == null) continue;
                all++;
                switch (r.Status)
                {
                    case "sold":
                        sold++;
                        break;
                    case "expired":
                        expired++;
                        break;
                    default:
                        active++;
                        break;
                }
            }

            return new { active, expired, sold, all };
        }

        /// <summary>
        /// Mirrors PlayerObject.MarketGetBack: an unsold listing with a bidder refunds the bid (always — the bidder already paid);
        /// returnItem mails an unsold item back, or for a sold listing pays out the seller's earnings (price/bid minus commission).
        /// </summary>
        public static MarketRemovalPlan PlanRemoval(MarketItemType type, bool sold, uint price, uint currentBid, bool hasBidder, bool returnItem)
        {
            uint refund = 0;
            if (!sold && hasBidder && type == MarketItemType.Auction) refund = currentBid;

            if (!returnItem) return new MarketRemovalPlan(false, 0, refund);

            if (!sold) return new MarketRemovalPlan(true, 0, refund);

            var cost = type == MarketItemType.Consign ? price : currentBid;
            var payout = (uint)Math.Max(0, cost - cost * Globals.Commission);
            return new MarketRemovalPlan(false, payout, refund);
        }

        public static List<MarketRow> Apply(IEnumerable<MarketRow> rows, string q, string by, string status, string sort, string dir)
        {
            var list = (rows ?? Enumerable.Empty<MarketRow>()).Where(r => r != null);

            var st = (status ?? "active").Trim().ToLowerInvariant();
            if (st == "active" || st == "expired" || st == "sold")
                list = list.Where(r => r.Status == st);

            q = q?.Trim();
            if (!string.IsNullOrEmpty(q))
            {
                var mode = (by ?? "any").Trim().ToLowerInvariant();
                var isId = ulong.TryParse(q.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var id);

                list = list.Where(r =>
                {
                    var itemHit = Contains(r.ItemName, q);
                    var sellerHit = Contains(r.Seller, q) || Contains(r.SellerAccount, q);

                    switch (mode)
                    {
                        case "item":
                            return itemHit;
                        case "seller":
                            return sellerHit;
                        case "buyer":
                            return Contains(r.Buyer, q);
                        default:
                            return itemHit || sellerHit || Contains(r.Buyer, q) || (isId && r.AuctionId == id);
                    }
                });
            }

            // No sort → newest listings first (unless dir=asc); an explicit sort is ascending unless dir=desc.
            var desc = string.IsNullOrWhiteSpace(sort)
                ? !string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase)
                : string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase);

            var key = (sort ?? "date").Trim().ToLowerInvariant();

            IOrderedEnumerable<MarketRow> ordered;
            switch (key)
            {
                case "id":
                    ordered = Order(list, r => r.AuctionId, desc);
                    break;
                case "item":
                    ordered = Order(list, r => r.ItemName ?? string.Empty, desc, StringComparer.OrdinalIgnoreCase);
                    break;
                case "count":
                    ordered = Order(list, r => r.Count, desc);
                    break;
                case "price":
                    ordered = Order(list, r => r.Price, desc);
                    break;
                case "bid":
                    ordered = Order(list, r => r.CurrentBid, desc);
                    break;
                case "seller":
                    ordered = Order(list, r => r.Seller ?? string.Empty, desc, StringComparer.OrdinalIgnoreCase);
                    break;
                case "expiry":
                    ordered = Order(list, r => r.Expiry, desc);
                    break;
                case "type":
                    ordered = Order(list, r => r.ItemType, desc);
                    break;
                case "status":
                    ordered = Order(list, r => r.Status ?? string.Empty, desc, StringComparer.Ordinal);
                    break;
                default:
                    ordered = Order(list, r => r.ConsignmentDate, desc);
                    break;
            }

            return ordered.ThenBy(r => r.AuctionId).ToList();
        }

        private static bool Contains(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack) && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IOrderedEnumerable<MarketRow> Order<TKey>(IEnumerable<MarketRow> rows, Func<MarketRow, TKey> key, bool desc, IComparer<TKey> comparer = null)
        {
            return desc ? rows.OrderByDescending(key, comparer) : rows.OrderBy(key, comparer);
        }
    }
}
