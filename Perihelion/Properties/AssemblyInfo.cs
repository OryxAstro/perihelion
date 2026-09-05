using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("Perihelion")]
[assembly: AssemblyDescription("Standalone comet/asteroid non-sidereal tracking for PiNStars/Touch-N-Stars.")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("OryxAstro")]
[assembly: AssemblyProduct("Perihelion")]
[assembly: AssemblyCopyright("Copyright © 2026 OryxAstro")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]
[assembly: Guid("263c5e4b-47d6-4ce1-83b8-b7b0b14ac6c9")]

[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]

// The minimum version of PiNStars/N.I.N.A. this plugin is compatible with.
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.0.0.1031")]

[assembly: AssemblyMetadata("Homepage", "https://www.buymeacoffee.com/OryxAstro")]
[assembly: AssemblyMetadata("License", "GPL-3.0-or-later")]
[assembly: AssemblyMetadata("LicenseURL", "https://github.com/OryxAstro/perihelion/blob/main/LICENSE")]
[assembly: AssemblyMetadata("Repository", "https://github.com/OryxAstro/perihelion")]
[assembly: AssemblyMetadata("ChangelogURL", "https://github.com/OryxAstro/perihelion/blob/main/CHANGELOG.md")]
[assembly: AssemblyMetadata("Tags", "Comet,Asteroid,Tracking,Orbital")]
[assembly: AssemblyMetadata("FeaturedImageURL", "https://raw.githubusercontent.com/OryxAstro/perihelion/main/docs/icon.png")]
[assembly: AssemblyMetadata("ScreenshotURL", "")]
[assembly: AssemblyMetadata("AltScreenshotURL", "")]
[assembly: AssemblyMetadata("LongDescription", @"Sets a mount's custom RA/Dec tracking rate for a comet or asteroid so it stays centered
in frame without fighting the sidereal rate. Computed live, on-device, from real orbital
elements — no external service or internet dependency in the field.")]
