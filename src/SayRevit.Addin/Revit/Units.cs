namespace SayRevit.Addin.Revit
{
    /// <summary>Conversioni tra millimetri e unità interne di Revit (piedi).</summary>
    public static class Units
    {
        public const double MmPerFoot = 304.8;

        public static double MmToFt(double mm)
        {
            return mm / MmPerFoot;
        }

        public static double FtToMm(double ft)
        {
            return ft * MmPerFoot;
        }
    }
}
