using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Threading.Channels;
using Newtonsoft.Json;

namespace JsonMaster.Api.Services;

public class StreamingDiffService
{

    public IAsyncEnumerable<List<string>> CompareStreamsAsync(Stream stream1, Stream stream2)
    {

        var channel = Channel.CreateBounded<List<string>>(new BoundedChannelOptions(50)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });


        Task.Run(async () => await CompareLogic(stream1, stream2, channel.Writer));

        return channel.Reader.ReadAllAsync();
    }

    private async Task CompareLogic(Stream stream1, Stream stream2, ChannelWriter<List<string>> writer)
    {
        try
        {
            using var reader1 = new JsonTextReader(new StreamReader(stream1));
            using var reader2 = new JsonTextReader(new StreamReader(stream2));

            int index = 0;
            var batch = new List<string>(1000); // Batch size 1000

            while (true)
            {
                // Synchronous read is fastest for local parsing
                bool hasNext1 = reader1.Read();
                bool hasNext2 = reader2.Read();

                if (!hasNext1 && !hasNext2) break;

                // Check mismatch
                bool isDiff = false;
                string? diffMsg = null;

                if (hasNext1 != hasNext2)
                {
                    diffMsg = $"{{\"diff\": \"Structure mismatch at index {index}: One stream ended early.\"}}";
                    isDiff = true;
                }
                else if (reader1.TokenType != reader2.TokenType)
                {
                     diffMsg = $"{{\"diff\": \"Token mismatch at path '{reader1.Path}': {reader1.TokenType} vs {reader2.TokenType}\"}}";
                     isDiff = true;
                }
                else if (reader1.Value != null && !reader1.Value.Equals(reader2.Value))
                {
                     diffMsg = $"{{\"diff\": \"Value mismatch at path '{reader1.Path}': {reader1.Value} vs {reader2.Value}\"}}";
                     isDiff = true;
                }

                if (isDiff && diffMsg != null)
                {
                    batch.Add(diffMsg);


                    if (batch.Count >= 1000)
                    {
                        await writer.WriteAsync(batch);
                        batch = new List<string>(1000);
                    }


                    if (hasNext1 != hasNext2) return; 
                }

                index++;
            }


            batch.Add("{\"status\": \"Comparison complete.\"}");
            if (batch.Count > 0)
            {
                await writer.WriteAsync(batch);
            }
        }
        catch (Exception ex)
        {
            await writer.WriteAsync(new List<string> { $"{{\"error\": \"Error reading streams: {ex.Message}\"}}" });
        }
        finally
        {
            writer.Complete();
        }
    }
}
