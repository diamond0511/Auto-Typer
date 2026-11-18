using System.Reflection;
using System.Runtime.InteropServices;

// Version and identity for the built exe. csc turns these into the Win32
// VERSIONINFO resource, so they are what Windows shows under Properties >
// Details, and what tells one build apart from another once the file has been
// copied to another machine or downloaded from a release page.
//
// AssemblyVersion is the one to bump for a release. Keep it in step with the
// git tag: tag v1.0 means 1.0.0.0 here.

[assembly: AssemblyTitle("Auto Typer")]
[assembly: AssemblyProduct("Auto Typer")]
[assembly: AssemblyDescription("Types the clipboard into the focused window, one real key at a time, at a human rhythm.")]
[assembly: AssemblyCompany("diamond0511")]
[assembly: AssemblyCopyright("Copyright (c) 2026")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]

// Nothing here is meant to be reached from COM.
[assembly: ComVisible(false)]
