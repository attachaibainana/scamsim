namespace ScamSim.Core
{
    /// <summary>
    /// Decides where each answer option sits on screen, so the comply option is not always first.
    /// order[slot] = option index (0 comply, 1 hesitate, 2 resist). The order comes from the session's
    /// option_shuffle_seed and the turn index with a small PRNG written here (not System.Random), so the
    /// same seed gives the same layout on every runtime. The order shown is also written to the log as
    /// option_display_order, so analysts never need to recompute it.
    /// </summary>
    public static class OptionOrder
    {
        public const int StandardCount = 3;

        public static int[] For(int seed, int turnIndex, int count = StandardCount)
        {
            var order = new int[count < 0 ? 0 : count];
            for (int i = 0; i < order.Length; i++) order[i] = i;

            unchecked
            {
                uint state = Mix((uint)seed ^ Mix((uint)turnIndex));
                for (int i = order.Length - 1; i > 0; i--)
                {
                    state = Mix(state);
                    int j = (int)(state % (uint)(i + 1));
                    int swap = order[i];
                    order[i] = order[j];
                    order[j] = swap;
                }
            }
            return order;
        }

        /// <summary>splitmix32 finaliser.</summary>
        static uint Mix(uint z)
        {
            unchecked
            {
                z += 0x9E3779B9u;
                z = (z ^ (z >> 16)) * 0x85EBCA6Bu;
                z = (z ^ (z >> 13)) * 0xC2B2AE35u;
                return z ^ (z >> 16);
            }
        }
    }
}
