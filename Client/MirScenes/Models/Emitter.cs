namespace Client.MirScenes.Models
{
    /// <summary>
    /// A tiny event (port of WebClient/src/game/emitter.ts): the models announce changes through these and a UI subscribes in
    /// its build and unsubscribes in its dispose. UI thread only. Copy-on-write handler array: Emit allocates nothing and a
    /// handler may unsubscribe while it runs.
    /// </summary>
    public sealed class Emitter
    {
        private Action[] _handlers = Array.Empty<Action>();

        public void On(Action handler)
        {
            if (handler == null) return;
            Action[] next = new Action[_handlers.Length + 1];
            Array.Copy(_handlers, next, _handlers.Length);
            next[_handlers.Length] = handler;
            _handlers = next;
        }

        public void Off(Action handler)
        {
            int index = Array.IndexOf(_handlers, handler);
            if (index < 0) return;
            Action[] next = new Action[_handlers.Length - 1];
            Array.Copy(_handlers, 0, next, 0, index);
            Array.Copy(_handlers, index + 1, next, index, _handlers.Length - index - 1);
            _handlers = next;
        }

        public void Emit()
        {
            Action[] handlers = _handlers;
            for (int i = 0; i < handlers.Length; i++)
                handlers[i]();
        }
    }

    /// <summary>An <see cref="Emitter"/> that carries a value.</summary>
    public sealed class Emitter<T>
    {
        private Action<T>[] _handlers = Array.Empty<Action<T>>();

        public void On(Action<T> handler)
        {
            if (handler == null) return;
            Action<T>[] next = new Action<T>[_handlers.Length + 1];
            Array.Copy(_handlers, next, _handlers.Length);
            next[_handlers.Length] = handler;
            _handlers = next;
        }

        public void Off(Action<T> handler)
        {
            int index = Array.IndexOf(_handlers, handler);
            if (index < 0) return;
            Action<T>[] next = new Action<T>[_handlers.Length - 1];
            Array.Copy(_handlers, 0, next, 0, index);
            Array.Copy(_handlers, index + 1, next, index, _handlers.Length - index - 1);
            _handlers = next;
        }

        public void Emit(T value)
        {
            Action<T>[] handlers = _handlers;
            for (int i = 0; i < handlers.Length; i++)
                handlers[i](value);
        }
    }
}
