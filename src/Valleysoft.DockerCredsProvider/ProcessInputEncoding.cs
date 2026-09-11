using System.Diagnostics;
using System.Security;
using System.Text;

namespace Valleysoft.DockerCredsProvider;

internal static class ProcessInputEncoding
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static void Configure(
        ProcessStartInfo startInfo,
        string? input,
        bool forceFallback,
        Encoding? fallbackEncoding)
    {
        if (!startInfo.RedirectStandardInput)
        {
            return;
        }

        try
        {
#if !NETSTANDARD2_0
            if (!forceFallback)
            {
                startInfo.StandardInputEncoding = Utf8;
                return;
            }
#endif

            // Without a compile-time stdin encoding API, validate the host encoding instead of changing it.
            Encoding encoding = fallbackEncoding ?? Console.InputEncoding;
            string payload = input is null ? string.Empty : input + Environment.NewLine;
            if (encoding.GetPreamble().Length > 0 ||
                !encoding.GetBytes(payload).SequenceEqual(Utf8.GetBytes(payload)))
            {
                throw new ProcessStreamException(
                    "standard input",
                    exitCode: null,
                    new InvalidOperationException(
                        $"The runtime cannot safely write credential-helper input using code page {encoding.CodePage}."));
            }
        }
        catch (Exception e) when (
            e is not ProcessStreamException &&
            e is IOException or SecurityException or EncoderFallbackException)
        {
            throw new ProcessStreamException("standard input", exitCode: null, e);
        }
    }
}
