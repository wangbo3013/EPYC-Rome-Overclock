using System.Collections.Generic;

namespace RomeOverclock
{
    public class ValueSaver
    {
        private static Dictionary<string, int> values = new Dictionary<string, int>();

        public static void AddValue(string key, int val)
        {
            values[key] = val;
        }

        public static int GetValue(string key)
        {
            int r;
            bool d = values.TryGetValue(key, out r);
            if (d)
            {
                return r;
            }

            return 0;
        }
    }
}