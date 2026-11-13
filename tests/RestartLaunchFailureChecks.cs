using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;
using KellysJOINCHECK;

internal static class RestartLaunchFailureRegression
{
    internal static void Run(Action<bool, string> check)
    {
        check(RestartLaunchFailure.Classify(null) == RestartLaunchFailureKind.Unknown, "absent launch evidence remains unknown");
        check(RestartLaunchFailure.Classify(new IOException("SmartScreen timeout blocked")) == RestartLaunchFailureKind.Unknown, "launch classification does not infer a security product or failure type from message text");
        check(RestartLaunchFailure.Classify(new IOException("outer", new TimeoutException("deadline"))) == RestartLaunchFailureKind.TimedOut, "a typed nested launch timeout has actionable manual restart guidance");
        check(RestartLaunchFailure.Classify(new IOException("outer", new Win32Exception(1223))) == RestartLaunchFailureKind.Cancelled, "nested Windows operation cancellation is retained");
        check(RestartLaunchFailure.Classify(new COMException("cancel", unchecked((int)0x800704c7))) == RestartLaunchFailureKind.Cancelled, "HRESULT_FROM_WIN32 cancellation survives COM wrapping");
        foreach (int code in new[] { 5, 1260, 225, 226 })
        {
            check(RestartLaunchFailure.Classify(new Win32Exception(code)) == RestartLaunchFailureKind.Blocked, "native denied or policy-blocked launch is actionable without a SmartScreen verdict: " + code);
            check(RestartLaunchFailure.Classify(new COMException("denied", unchecked((int)(0x80070000u | (uint)code)))) == RestartLaunchFailureKind.Blocked, "corresponding denied HRESULT is recognized: " + code);
        }
        check(RestartLaunchFailure.Classify(new COMException("different facility", unchecked((int)0x800404c7))) == RestartLaunchFailureKind.Unknown, "unrelated HRESULT facilities cannot be mistaken for Windows cancellation");
        check(RestartLaunchFailure.Classify(new Win32Exception(2)) == RestartLaunchFailureKind.Unknown, "a missing helper file is not labelled a Windows security block");
        check(RestartLaunchFailure.Classify(new AggregateException(new TimeoutException(), new Win32Exception(1260))) == RestartLaunchFailureKind.Blocked, "actual policy denial survives aggregate errors and takes priority over a concurrent timeout");
        Exception nested = new Win32Exception(1260);
        for (int i = 0; i < RestartLaunchFailure.MaximumErrors; i++) nested = new IOException("layer", nested);
        check(RestartLaunchFailure.Classify(nested) == RestartLaunchFailureKind.Unknown, "untrusted nested exception inspection is bounded");
        foreach (Exception error in new Exception[] { new IOException("detailed original failure"), new TimeoutException(), new Win32Exception(1223), new Win32Exception(1260) })
        {
            string text = RestartLaunchFailure.Guidance(error);
            check(text.Contains("If Windows shows a security prompt, close it.") && text.Contains("Choose Restart manually") && text.Contains("reopen Nuclear Option through Steam"), "each launcher failure explains the safe manual fallback: " + error.GetType().Name);
            check(!text.Contains("SmartScreen") && !text.Contains("disable") && !text.Contains("Run anyway") && !text.Contains(error.Message), "player guidance does not claim a security diagnosis, bypass controls, or expose raw exception text: " + error.GetType().Name);
        }
        var original = new IOException("immutable support cause", new Win32Exception(1223));
        Exception originalInner = original.InnerException!;
        _ = RestartLaunchFailure.Guidance(original);
        check(original.Message == "immutable support cause" && ReferenceEquals(original.InnerException, originalInner), "formatting guidance leaves exact underlying failure available to Copy report");
        EvidenceChecks(check);
    }

    private static void EvidenceChecks(Action<bool, string> check)
    {
        var cancelled = new IOException("private outer path C:\\Users\\Tester\\secret", new COMException("private COM argument", unchecked((int)0x800704c7)));
        cancelled.Data["private field"] = "private user token";
        string evidence = RestartLaunchFailure.Evidence(cancelled);
        check(evidence.Contains("IOException HRESULT 0x") && evidence.Contains("COMException HRESULT 0x800704C7, Win32 1223"), "restart log evidence retains wrapper type and underlying COM cancellation code");
        check(!evidence.Contains("private") && !evidence.Contains("Tester") && !evidence.Contains("Users") && !evidence.Contains("secret") && !evidence.Contains("token"), "exception evidence never includes raw messages, Data, arguments, paths or stack traces");
        check(RestartLaunchFailure.Evidence(new Win32Exception(1260)).Contains("Win32Exception HRESULT 0x") && RestartLaunchFailure.Evidence(new Win32Exception(1260)).Contains(", Win32 1260"), "native Win32 denial code is preserved separately from its framework HRESULT");
        check(!RestartLaunchFailure.Evidence(new COMException("unrelated", unchecked((int)0x800404c7))).Contains("Win32"), "evidence does not invent native codes for other HRESULT facilities");
        check(RestartLaunchFailure.Evidence(new AggregateException(new TimeoutException("private"), new COMException("private", unchecked((int)0x80070005)))).Contains("TimeoutException HRESULT 0x") && RestartLaunchFailure.Evidence(new AggregateException(new TimeoutException(), new COMException("private", unchecked((int)0x80070005)))).Contains("COMException HRESULT 0x80070005, Win32 5"), "bounded aggregate evidence preserves concurrent timeout and access denial causes");
        Exception deep = new COMException("private deepest", unchecked((int)0x800704ec));
        for (int i = 0; i < RestartLaunchFailure.MaximumErrors; i++) deep = new IOException("private layer", deep);
        evidence = RestartLaunchFailure.Evidence(deep);
        check(evidence.Split(new[] { " HRESULT " }, StringSplitOptions.None).Length - 1 == RestartLaunchFailure.MaximumErrors && !evidence.Contains("COMException"), "evidence uses the same sixteen-error inspection bound as player guidance");
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("DoormanExceptionEvidenceFixture"), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule("exceptions").DefineType(new string('X', 512) + "Exception", TypeAttributes.Public, typeof(Exception));
        type.DefineDefaultConstructor(MethodAttributes.Public);
        Type longType = type.CreateType()!;
        Exception wide = new AggregateException(Enumerable.Range(0, 100).Select(_ => (Exception)Activator.CreateInstance(longType)!));
        evidence = RestartLaunchFailure.Evidence(wide);
        check(Encoding.UTF8.GetByteCount(evidence) <= RestartLaunchFailure.MaximumEvidenceChars && evidence.All(c => c <= 127), "wide aggregate and long type-name evidence remain within two KiB using ASCII-only metadata");
        check(evidence.Split(new[] { " HRESULT " }, StringSplitOptions.None).Length - 1 <= RestartLaunchFailure.MaximumErrors && !evidence.Contains(new string('X', 97)), "long exception names and large aggregate collections cannot defeat the evidence bounds");
        check(RestartLaunchFailure.Evidence(null) == "No exception evidence.", "missing evidence is explicitly unknown without fabricated Windows codes");
        check(RestartLaunchFailure.Evidence(new ThrowingMessageException()).StartsWith("ThrowingMessageException HRESULT 0x", StringComparison.Ordinal), "safe evidence never calls an exception's potentially sensitive or failing Message getter");
    }

    private sealed class ThrowingMessageException : Exception
    {
        public override string Message => throw new InvalidOperationException("Exception messages must not be read by the evidence formatter.");
    }
}
