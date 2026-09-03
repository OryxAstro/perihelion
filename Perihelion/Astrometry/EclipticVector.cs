namespace Perihelion.Astrometry {

    /// <summary>
    /// A Cartesian position in AU, heliocentric or geocentric ecliptic (mean equinox J2000)
    /// depending on context. Mirrors OryxAstro's EclipticVector (orbitalMechanics.ts).
    /// </summary>
    public readonly struct EclipticVector {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public EclipticVector(double x, double y, double z) {
            X = x;
            Y = y;
            Z = z;
        }

        public double Length() => System.Math.Sqrt(X * X + Y * Y + Z * Z);

        public double Dot(EclipticVector other) => X * other.X + Y * other.Y + Z * other.Z;

        public static EclipticVector operator -(EclipticVector a, EclipticVector b) =>
            new EclipticVector(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    }
}
