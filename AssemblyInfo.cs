using System.Runtime.CompilerServices;

// The self-checks drive the pure usage update (FrequentFiles.Record with an
// explicit list) directly; the app itself only uses the guarded entry point.
[assembly: InternalsVisibleTo("Kobold.FileSearchCheck")]
