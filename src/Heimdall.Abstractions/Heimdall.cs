using System;
using System.Collections.Generic;

// Hintereinanderliegende Dateien, alle im Namensraum Heimdall.
// Inhalt:
//   HeimdallAttributes.cs   - Schluessel/Wert-Attribute + Helfer
//   HeimdallModel.cs        - kanonische Records (HSpan, HLogRecord, HMetricPoint, ...)
//   HeimdallInterfaces.cs   - IHeimdallTracer/Logger/Meter/Span/Sink/Hub
//   HeimdallNoop.cs         - Null-Implementierungen (Default-Overhead 0)
namespace Heimdall;