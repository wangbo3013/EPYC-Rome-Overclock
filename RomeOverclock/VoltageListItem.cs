namespace RomeOverclock
{
    public class VoltageListItem
    {
        public int VID { get; set; }
        public double Voltage { get; set; }

        public VoltageListItem(int vid, double voltage)
        {
            VID = vid;
            Voltage = voltage;
        }

        public override string ToString()
        {
            return $"{Voltage}V";
        }
    }
}