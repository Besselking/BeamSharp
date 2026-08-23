; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
BS1001 | BeamSharp.Serialization | Error | Serialization context must be partial
BS1002 | BeamSharp.Serialization | Error | Serialization context must derive from ErlSerializerContext
BS1003 | BeamSharp.Serialization | Error | Type cannot be constructed during deserialization
BS1004 | BeamSharp.Serialization | Error | Type cannot have a converter generated for it
BS1005 | BeamSharp.Serialization | Warning | Tagged tuple shape depends on member order
