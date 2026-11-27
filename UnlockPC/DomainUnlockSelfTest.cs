using System.ComponentModel;
using System.Security.Principal;

namespace UnlockPC;

internal static class DomainUnlockSelfTest
{
    private const string Machine = "TESTPC";
    private const string Domain = "UNLOCKLAB";
    private const string Username = "tester";
    private const uint Session = 4;
    private static readonly SecurityIdentifier DomainSid = new("S-1-5-21-101-102-103-1101");
    private static readonly SecurityIdentifier LocalSid = new("S-1-5-21-201-202-203-1101");

    internal static int Run()
    {
        try
        {
            CheckAccounts();
            CheckPassword();
            CheckRegistration();
            BluetoothLog.Write("domain_unlock_self_test_passed");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            BluetoothLog.Write("domain_unlock_self_test_failed", new { ex.Message });
            return 1;
        }
    }

    private static void CheckAccounts()
    {
        Check(DirectUnlock.GetConsoleAccount($@"{Domain}\{Username}", DomainSid, Machine, Session, Session) ==
            (Username, Domain, Session), "domain account retains the Windows domain");
        Check(DirectUnlock.GetConsoleAccount($@"testpc\{Username}", LocalSid, Machine, Session, Session) ==
            (Username, ".", Session), "local account uses the local machine alias regardless of case");
        ExpectRejection(() => DirectUnlock.GetConsoleAccount($@"{Domain}\{Username}", DomainSid, Machine, Session, 9),
            "a non-console session");
        ExpectRejection(() => DirectUnlock.GetConsoleAccount($@"{Domain}\{Username}", DomainSid, Machine, Session, uint.MaxValue),
            "no active console session");
        foreach (string invalidName in new[] { Username, @"\tester", @"UNLOCKLAB\" })
            ExpectRejection(() => DirectUnlock.GetConsoleAccount(invalidName, DomainSid, Machine, Session, Session),
                $"invalid account name: {invalidName}");
        ExpectRejection(() => DirectUnlock.GetConsoleAccount(@"NT AUTHORITY\SYSTEM",
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), Machine, Session, Session), "a system SID");
        ExpectRejection(() => DirectUnlock.GetConsoleAccount(@"AzureAD\tester",
            new SecurityIdentifier("S-1-12-1-1-2-3-4"), Machine, Session, Session), "a cloud SID");
        ExpectRejection(() => DirectUnlock.GetConsoleAccount($@"{Domain}\{Username}", null, Machine, Session, Session),
            "a missing SID");
    }

    private static unsafe void CheckPassword()
    {
        var password = new char[DirectUnlock.PasswordBytes / sizeof(char)];
        password[0] = 'x';
        fixed (char* characters = password)
        {
            nint buffer = (nint)characters;
            var account = DirectUnlock.GetConsoleAccount($@"{Domain}\{Username}", DomainSid, Machine, Session, Session);
            bool authenticated = false;
            DirectUnlock.ValidatePassword(account.Username, account.Domain, DomainSid, buffer, (user, domain, input) =>
            {
                Check((user, domain, input) == (Username, Domain, buffer), "authentication receives the domain and user separately");
                authenticated = true;
                return new SecurityIdentifier(DomainSid.Value);
            });
            Check(authenticated, "matching SID is authenticated");
            ExpectRejection(() => DirectUnlock.ValidatePassword(account.Username, account.Domain, DomainSid, buffer,
                (_, _, _) => LocalSid), "same-name local account with a different SID");
            ExpectRejection(() => DirectUnlock.ValidatePassword(account.Username, account.Domain, DomainSid, buffer,
                (_, _, _) => null), "token without a user SID");
            ExpectRejection(() => DirectUnlock.ValidatePassword(account.Username, account.Domain, null, buffer,
                (_, _, _) => throw new Exception("Authentication ran without an expected account SID.")), "missing expected SID");

            try
            {
                DirectUnlock.ValidatePassword(account.Username, account.Domain, DomainSid, buffer,
                    (_, _, _) => throw new Win32Exception(1326));
                throw new InvalidOperationException("Authentication failure was swallowed.");
            }
            catch (Win32Exception ex)
            {
                Check(DirectUnlock.IsPasswordRejected(ex), "incorrect domain password remains a password rejection");
            }

            password[0] = '\0';
            ExpectRejection(() => DirectUnlock.ValidatePassword(account.Username, account.Domain, DomainSid, buffer,
                (_, _, _) => throw new Exception("An empty password reached authentication.")), "empty password");
            Array.Fill(password, 'x');
            ExpectRejection(() => DirectUnlock.ValidatePassword(account.Username, account.Domain, DomainSid, buffer,
                (_, _, _) => throw new Exception("An unterminated password reached authentication.")), "unterminated password");
        }
    }

    private static void CheckRegistration()
    {
        const string installed = @"C:\Test\Provider-current.dll";
        const string stale = @"C:\Test\Provider-old.dll";
        static bool FileExists(string path) => path == installed;
        Check(ProviderRegistration.GetInstalledPath(true, installed, null, false, FileExists) == installed,
            "local accounts can use the legacy registration");
        Check(ProviderRegistration.GetInstalledPath(true, installed, null, true, FileExists) is null,
            "domain accounts require the domain support marker");
        Check(ProviderRegistration.GetInstalledPath(true, installed, stale, true, FileExists) is null,
            "a marker for another DLL does not authorize the current registration");
        Check(ProviderRegistration.GetInstalledPath(true, installed, installed.ToUpperInvariant(), true, FileExists) == installed,
            "domain support marker accepts equivalent Windows path casing");
        Check(ProviderRegistration.GetInstalledPath(true, stale, stale, true, FileExists) is null,
            "a missing DLL cannot satisfy registration");
        Check(ProviderRegistration.GetInstalledPath(false, installed, installed, true, FileExists) is null,
            "the provider registry entry is required");
        Check(ProviderRegistration.GetInstalledPath(true, null, installed, true, FileExists) is null,
            "the COM DLL path is required");
    }

    private static void ExpectRejection(Action action, string description)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException($"Expected rejection: {description}.");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException($"Check failed: {description}.");
    }
}
