#nullable enable

namespace Diggles.Runtime.Tcl
{
    using System;

    // Tcl error. Wraps both real errors (syntax, unknown command, etc.)
    // and Tcl-level conditions like return/break/continue that propagate
    // up the call stack via exceptions.
    public class TclException : Exception
    {
        public int LineNumber { get; }

        public TclException(string message, int lineNumber = 0)
            : base(lineNumber > 0 ? $"line {lineNumber}: {message}" : message)
        {
            LineNumber = lineNumber;
        }

        public TclException(string message, Exception inner, int lineNumber = 0)
            : base(lineNumber > 0 ? $"line {lineNumber}: {message}" : message, inner)
        {
            LineNumber = lineNumber;
        }
    }

    // Non-error Tcl flow-control "exceptions" — caught by control structures.
    // Mirror standard Tcl return codes:
    // TCL_OK = 0 (normal return)
    // TCL_ERROR = 1
    // TCL_RETURN = 2
    // TCL_BREAK = 3
    // TCL_CONTINUE = 4

    public sealed class TclReturnException : Exception
    {
        public string Value { get; }
        public TclReturnException(string value) { Value = value; }
    }

    public sealed class TclBreakException : Exception { }
    public sealed class TclContinueException : Exception { }
}
