namespace TrackStash.Catalog.Api.Authentication;

internal static class AdminPasswordCommand
{
    internal static int Run(string[] args)
    {
        if (args.Length != 1 || Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Run hash-admin-password with no arguments from an interactive terminal.");
            return 1;
        }

        var password = ReadPassword("New administrator password: ");
        var confirmation = ReadPassword("Confirm administrator password: ");
        if (password is null || password.Length < AdminCredentials.MinPasswordLength || password != confirmation)
        {
            Console.Error.WriteLine($"Passwords must match and contain {AdminCredentials.MinPasswordLength}–{AdminCredentials.MaxPasswordLength} characters.");
            return 1;
        }
        Console.WriteLine(AdminCredentials.CreateHasher().HashPassword(new object(), password));
        return 0;
    }

    private static string? ReadPassword(string prompt)
    {
        Console.Error.Write(prompt);
        Span<char> buffer = stackalloc char[AdminCredentials.MaxPasswordLength];
        var length = 0;
        var overflow = 0;
        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) return overflow == 0 ? new string(buffer[..length]) : null;
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (overflow > 0) overflow--;
                    else if (length > 0) buffer[--length] = '\0';
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    if (length < buffer.Length) buffer[length++] = key.KeyChar;
                    else overflow++;
                }
            }
        }
        finally
        {
            buffer.Clear();
            Console.Error.WriteLine();
        }
    }
}
