using System.Runtime.CompilerServices;

// The self-checks drive the pure usage update (FrequentFiles.Record with an
// explicit list) directly; the app itself only uses the guarded entry point.
[assembly: InternalsVisibleTo("Kobold.FileSearchCheck")]
// The XamlLoadCheck pins drive the internal view host interfaces and the
// widget-to-view factory (Task T2), so the fake host can live in the test
// assembly instead of the production one.
[assembly: InternalsVisibleTo("Kobold.XamlLoadCheck")]
// The menu-render pin builds the group window's tab/current menu through the
// same internal builder the window uses (Task T4).
[assembly: InternalsVisibleTo("Kobold.MenuRenderCheck")]
// The control checks drive the internal AppDiagnostics test seams (Task 2).
[assembly: InternalsVisibleTo("Kobold.ControlCheck")]
