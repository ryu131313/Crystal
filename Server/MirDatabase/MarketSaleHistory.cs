namespace Server.MirDatabase
{
    public struct MarketSale
    {
        public uint Price;
        public DateTime Date;
    }

    /// <summary>
    /// v119: the last <see cref="Capacity"/> completed market sales (consignments bought, auctions won) of one item
    /// index, kept as a ring. Saved in Server.MirADB after the respawn block (Envir.SaveAccounts). Game thread only.
    /// </summary>
    public class MarketSaleHistory
    {
        public const int Capacity = 14;

        private readonly MarketSale[] _sales = new MarketSale[Capacity];
        private int _start;

        public int Count { get; private set; }

        public MarketSaleHistory() { }

        public MarketSaleHistory(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                uint price = reader.ReadUInt32();
                DateTime date = DateTime.FromBinary(reader.ReadInt64());
                Add(price, date);
            }
        }

        /// <summary>Adds a sale; past <see cref="Capacity"/> the oldest one is overwritten.</summary>
        public void Add(uint price, DateTime date)
        {
            int slot;
            if (Count < Capacity)
            {
                slot = (_start + Count) % Capacity;
                Count++;
            }
            else
            {
                slot = _start;
                _start = (_start + 1) % Capacity;
            }

            _sales[slot] = new MarketSale { Price = price, Date = date };
        }

        /// <summary>The sale at <paramref name="index"/>, 0 = oldest kept.</summary>
        public MarketSale this[int index]
        {
            get
            {
                if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
                return _sales[(_start + index) % Capacity];
            }
        }

        public void Save(BinaryWriter writer)
        {
            writer.Write(Count);
            for (int i = 0; i < Count; i++)
            {
                MarketSale sale = _sales[(_start + i) % Capacity];
                writer.Write(sale.Price);
                writer.Write(sale.Date.ToBinary());
            }
        }
    }
}
