namespace Shared.Extensions
{
    public static class HelperExtensions
    {
        // Random.Shared is thread-safe; a shared new Random() was not.
        private static Random rng => Random.Shared;

        public static T ValueOrDefault<T>(this object value)
        {
            if (value == null || value.ToString() == "")
            {
                return default;
            }

            return (T)Convert.ChangeType(value, typeof(T));
        }

        public static void Shuffle<T>(this IList<T> list)
        {
            int n = list.Count;
            while (n > 1)
            {
                n--;
                int k = rng.Next(n + 1);
                T value = list[k];
                list[k] = list[n];
                list[n] = value;
            }
        }
    }
}
