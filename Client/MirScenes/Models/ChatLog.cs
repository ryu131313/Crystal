namespace Client.MirScenes.Models
{
    /// <summary>One received chat line, as it arrived (no colours: those are the UI's).</summary>
    public sealed class ChatLine
    {
        public string Text;
        public ChatType Type;
        /// <summary>CMain.Time when it arrived.</summary>
        public long Time;
    }

    /// <summary>
    /// Every chat / system line the client shows (port of WebClient/src/game/chatLog.ts). The one entry point for chat:
    /// code that used to call ChatDialog.ReceiveChat calls <see cref="Add"/>; the classic ChatDialog subscribes to
    /// <see cref="Added"/> and renders the line, a modern chat window reads <see cref="Lines"/>.
    /// </summary>
    public sealed class ChatLog
    {
        public const int MaxLines = 500;

        public readonly List<ChatLine> Lines = new List<ChatLine>();
        public readonly Emitter<ChatLine> Added = new Emitter<ChatLine>();

        public void Add(string text, ChatType type)
        {
            if (text == null) return;

            ChatLine line = new ChatLine { Text = text, Type = type, Time = CMain.Time };
            if (Lines.Count >= MaxLines) Lines.RemoveAt(0);
            Lines.Add(line);
            Added.Emit(line);
        }
    }
}
