using Xunit;

// Host-Boot-Tests nutzen Prozess-Umgebungsvariablen zur Temp-DB-Konfiguration (Program.cs
// liest builder.Configuration inkl. Env-Provider). Da Env-Vars prozessglobal sind, werden
// alle Tests serialisiert — kein Parallelisierungsrace beim Host-Boot.
[assembly: CollectionBehavior(DisableTestParallelization = true)]