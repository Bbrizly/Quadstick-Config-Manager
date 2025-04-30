using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace QSCM.Services;

public static class QmpBridge
{
    // Save CSV to a temp file then ask QMP to import it.
    public static async Task<bool> ImportCsvAsync(byte[] csvBytes)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"qscm_{Guid.NewGuid()}.csv");
        await File.WriteAllBytesAsync(tmp, csvBytes);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", 7001);
            var json =
                $"{{\"action\":\"IMPORT_CSV\",\"path\":\"{tmp.Replace("\\", "\\\\")}\"}}";
            var data = Encoding.ASCII.GetBytes(json + "\n");
            await client.GetStream().WriteAsync(data);
            // QMP replies "OK\n" on success
            var buffer = new byte[64];
            var n = await client.GetStream().ReadAsync(buffer);
            return Encoding.ASCII.GetString(buffer, 0, n).StartsWith("OK");
        }
        catch
        {
            return false; // QMP not running
        }
    }
}
