namespace RomeOverclock
{
    public class FrequencyListItem
    {
        public int frequency { get; }
        public string display { get; }

        public FrequencyListItem(int frequency, string display)
        {
            this.frequency = frequency;
            this.display = display;
        }

        public override string ToString()
        {
            return display;
        }
    }
}