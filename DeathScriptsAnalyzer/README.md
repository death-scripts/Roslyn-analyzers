+# ConstructorLengthAnalyzer

+Detects C# constructors where the declaration line exceeds 100 characters and offers a code fix to place each parameter on a new line.

+- Diagnostic ID: `DS0001`
+- Severity: Info
+- Trigger: Constructor declaration line length > 100 chars
+- Fix: Each parameter moved to its own line with indentation based on the constructor start column + 4 spaces.

+## Notes
+- The analyzer measures only the first line containing the constructor start. If a constructor is already wrapped across lines so the first line is 100 characters or less, no diagnostic is reported.
+- The code fix preserves leading comments on parameters and relies on the Roslyn formatter to clean up spaces/indentation.

+## Building
+Add the project to a solution and build. The analyzer assembly can be referenced by other projects or packed as a NuGet analyzer package if desired.
