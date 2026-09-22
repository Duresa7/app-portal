using System;

namespace AppPortal.Client.Services;

/// <summary>
/// The PC and the account the demo data is about. Normally this PC and the person running it, so the
/// demo reads as theirs; a screenshot switches to sample names, because a picture that goes into the
/// public docs must not carry the name of whichever machine and account happened to take it.
/// </summary>
public static class DemoIdentity
{
    public static string MachineName { get; private set; } = Environment.MachineName;

    public static string Account { get; private set; } = WindowsAccount.Current();

    public static void UseSamples()
    {
        MachineName = "OBIPC";
        Account = @"CONTOSO\obi";
    }
}
