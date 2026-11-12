using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

var options = Options.Read(args);
var authentication = JsonSerializer.Deserialize<Auth>(await File.ReadAllTextAsync(options.AuthenticationFile))
    ?? throw new InvalidOperationException("SMTP authentication configuration is invalid.");
if (string.IsNullOrWhiteSpace(authentication.User) || string.IsNullOrEmpty(authentication.Password))
    throw new InvalidOperationException("SMTP authentication configuration is incomplete.");
var certificate = X509CertificateLoader.LoadPkcs12FromFile(options.CertificateFile, null,
    X509KeyStorageFlags.EphemeralKeySet);
Directory.CreateDirectory(options.MailboxDirectory);
var listener = new TcpListener(IPAddress.Any, options.Port);
listener.Start();
Console.WriteLine("Cohort N protected STARTTLS SMTP fixture is ready.");
while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    _ = Task.Run(() => Session.RunAsync(client, certificate, authentication, options.MailboxDirectory));
}

sealed record Auth(string User, string Password);

sealed record Options(int Port, string CertificateFile, string AuthenticationFile, string MailboxDirectory)
{
    public static Options Read(string[] values)
    {
        string Required(string name)
        {
            var index = Array.IndexOf(values, name);
            if (index < 0 || index + 1 >= values.Length || string.IsNullOrWhiteSpace(values[index + 1]))
                throw new InvalidOperationException($"Missing {name}.");
            return values[index + 1];
        }
        return new(int.Parse(Required("--port"), System.Globalization.CultureInfo.InvariantCulture),
            Required("--certificate"), Required("--authentication"), Required("--mailbox"));
    }
}

static class Session
{
    public static async Task RunAsync(TcpClient client, X509Certificate2 certificate, Auth authentication,
        string mailboxDirectory)
    {
        using (client)
        {
            try
            {
                await using var network = client.GetStream();
                await WriteAsync(network, "220 cohort-n-smtp ESMTP ready\r\n");
                Stream stream = network;
                var secured = false;
                var authenticated = false;
                var recipient = "";
                while (true)
                {
                    var line = await ReadLineAsync(stream);
                    if (line is null) return;
                    if (line.StartsWith("EHLO ", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("HELO ", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsync(stream, secured
                            ? "250-cohort-n-smtp\r\n250-AUTH LOGIN PLAIN\r\n250 SIZE 1048576\r\n"
                            : "250-cohort-n-smtp\r\n250-STARTTLS\r\n250 SIZE 1048576\r\n");
                    }
                    else if (line.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase) && !secured)
                    {
                        await WriteAsync(stream, "220 Ready to start TLS\r\n");
                        var tls = new SslStream(stream, leaveInnerStreamOpen: true);
                        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificate = certificate,
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            ClientCertificateRequired = false,
                        });
                        stream = tls;
                        secured = true;
                    }
                    else if (line.StartsWith("AUTH LOGIN", StringComparison.OrdinalIgnoreCase) && secured)
                    {
                        var supplied = line.Length > 10 ? line[10..].Trim() : "";
                        if (supplied.Length == 0)
                        {
                            await WriteAsync(stream, "334 VXNlcm5hbWU6\r\n");
                            supplied = await ReadLineAsync(stream) ?? "";
                        }
                        await WriteAsync(stream, "334 UGFzc3dvcmQ6\r\n");
                        var password = await ReadLineAsync(stream) ?? "";
                        authenticated = FixedEquals(Decode(supplied), authentication.User) &&
                            FixedEquals(Decode(password), authentication.Password);
                        await WriteAsync(stream, authenticated ? "235 Authentication successful\r\n" : "535 Authentication failed\r\n");
                    }
                    else if (line.StartsWith("AUTH PLAIN ", StringComparison.OrdinalIgnoreCase) && secured)
                    {
                        var fields = Decode(line[11..].Trim()).Split('\0');
                        authenticated = fields.Length >= 3 && FixedEquals(fields[^2], authentication.User) &&
                            FixedEquals(fields[^1], authentication.Password);
                        await WriteAsync(stream, authenticated ? "235 Authentication successful\r\n" : "535 Authentication failed\r\n");
                    }
                    else if (line.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsync(stream, authenticated ? "250 Sender accepted\r\n" : "530 Authentication required\r\n");
                    }
                    else if (line.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase) && authenticated)
                    {
                        recipient = line[8..].Trim().Trim('<', '>');
                        await WriteAsync(stream, "250 Recipient accepted\r\n");
                    }
                    else if (line.Equals("DATA", StringComparison.OrdinalIgnoreCase) && authenticated && recipient.Length > 0)
                    {
                        await WriteAsync(stream, "354 End data with <CR><LF>.<CR><LF>\r\n");
                        var body = new StringBuilder();
                        while (true)
                        {
                            var dataLine = await ReadLineAsync(stream);
                            if (dataLine is null) return;
                            if (dataLine == ".") break;
                            if (dataLine.StartsWith("..", StringComparison.Ordinal)) dataLine = dataLine[1..];
                            body.AppendLine(dataLine);
                            if (body.Length > 1_048_576) throw new InvalidOperationException("SMTP message exceeds fixture limit.");
                        }
                        var record = JsonSerializer.Serialize(new
                        {
                            ReceivedAt = DateTimeOffset.UtcNow,
                            Recipient = recipient,
                            Message = body.ToString(),
                        });
                        var path = Path.Combine(mailboxDirectory, "messages.jsonl");
                        await File.AppendAllTextAsync(path, record + Environment.NewLine, new UTF8Encoding(false));
                        await WriteAsync(stream, "250 Message accepted\r\n");
                    }
                    else if (line.Equals("RSET", StringComparison.OrdinalIgnoreCase))
                    {
                        recipient = "";
                        await WriteAsync(stream, "250 Reset\r\n");
                    }
                    else if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsync(stream, "221 Closing connection\r\n");
                        return;
                    }
                    else
                    {
                        await WriteAsync(stream, "502 Command not implemented\r\n");
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or AuthenticationException or InvalidOperationException)
            {
                Console.WriteLine("SMTP fixture session ended without exposing message or credential data.");
            }
        }
    }

    private static string Decode(string value)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        catch (FormatException) { return ""; }
    }

    private static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private static async Task<string?> ReadLineAsync(Stream stream)
    {
        var bytes = new List<byte>(256);
        var one = new byte[1];
        while (bytes.Count <= 8192)
        {
            var count = await stream.ReadAsync(one);
            if (count == 0) return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            if (one[0] == (byte)'\n') break;
            if (one[0] != (byte)'\r') bytes.Add(one[0]);
        }
        if (bytes.Count > 8192) throw new InvalidOperationException("SMTP command exceeds fixture limit.");
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static Task WriteAsync(Stream stream, string value) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(value)).AsTask();
}
