using IotStarter.Shared;

namespace IotStarter.App.Core.Tests.Protocol;

public class FrameCodecTests
{
    [Theory]
    [InlineData(1, 20)]
    [InlineData(18, 20)]
    [InlineData(19, 20)]
    [InlineData(132, 20)]
    [InlineData(512, 20)]
    [InlineData(300, 185)]
    [InlineData(512, 244)]
    public void Split_then_assemble_round_trips(int length, int chunkSize)
    {
        byte[] message = Enumerable.Range(0, length).Select(i => (byte)(i * 7)).ToArray();

        byte[][] chunks = FrameSplitter.Split(message, 42, chunkSize);

        Assert.All(chunks, c => Assert.InRange(c.Length, 3, chunkSize));
        Assert.All(chunks, c => Assert.Equal(42, c[1]));
        Assert.Equal(ProtocolConstants.ChunkEndFlag, chunks[^1][0] & ProtocolConstants.ChunkEndFlag);

        var assembler = new FrameAssembler();
        for (int i = 0; i < chunks.Length - 1; i++)
        {
            Assert.Equal(FrameStatus.InProgress, assembler.Accept(chunks[i], i));
        }

        Assert.Equal(FrameStatus.Complete, assembler.Accept(chunks[^1], chunks.Length));
        Assert.Equal(message, assembler.TakeMessage());
        Assert.Null(assembler.TakeMessage());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(513)]
    public void Split_rejects_empty_and_oversized_messages(int length)
    {
        Assert.Null(FrameSplitter.Split(new byte[length], 0, 20));
    }

    [Theory]
    [InlineData(19)]
    [InlineData(245)]
    public void Split_rejects_chunk_sizes_out_of_range(int chunkSize)
    {
        Assert.Null(FrameSplitter.Split(new byte[10], 0, chunkSize));
    }

    [Fact]
    public void Out_of_order_chunk_is_rejected_and_buffer_discarded()
    {
        byte[][] chunks = FrameSplitter.Split(new byte[60], 1, 20);
        var assembler = new FrameAssembler();

        Assert.Equal(FrameStatus.InProgress, assembler.Accept(chunks[0], 0));
        Assert.Equal(FrameStatus.Rejected, assembler.Accept(chunks[2], 1));
        Assert.Equal(ErrorCode.InvalidFrame, assembler.LastError);

        // The continuation is now also out of order: nothing is in progress.
        Assert.Equal(FrameStatus.Rejected, assembler.Accept(chunks[1], 2));
    }

    [Fact]
    public void Chunk_from_another_message_is_rejected()
    {
        byte[][] first = FrameSplitter.Split(new byte[60], 1, 20);
        byte[][] second = FrameSplitter.Split(new byte[60], 2, 20);
        var assembler = new FrameAssembler();

        assembler.Accept(first[0], 0);

        Assert.Equal(FrameStatus.Rejected, assembler.Accept(second[1], 1));
    }

    [Fact]
    public void Index_zero_restarts_assembly()
    {
        byte[][] abandoned = FrameSplitter.Split(new byte[60], 1, 20);
        byte[] message = [1, 2, 3, 4];
        byte[][] fresh = FrameSplitter.Split(message, 2, 20);
        var assembler = new FrameAssembler();

        assembler.Accept(abandoned[0], 0);

        Assert.Equal(FrameStatus.Complete, assembler.Accept(fresh[0], 1));
        Assert.Equal(message, assembler.TakeMessage());
    }

    [Fact]
    public void Stale_partial_message_is_dropped_after_timeout()
    {
        byte[][] chunks = FrameSplitter.Split(new byte[60], 1, 20);
        var assembler = new FrameAssembler();

        assembler.Accept(chunks[0], 1_000);

        Assert.Equal(FrameStatus.Rejected, assembler.Accept(chunks[1], 1_000 + ProtocolConstants.ReassemblyTimeoutMs + 1));
    }

    [Fact]
    public void Oversized_reassembly_is_rejected()
    {
        var assembler = new FrameAssembler();
        byte[] chunk = new byte[244];
        int index = 0;
        FrameStatus status;
        do
        {
            chunk[0] = (byte)index;
            chunk[1] = 9;
            status = assembler.Accept(chunk, index);
            index++;
        }
        while (status == FrameStatus.InProgress);

        Assert.Equal(FrameStatus.Rejected, status);
        Assert.Equal(ErrorCode.InvalidPayload, assembler.LastError);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x80 })]
    [InlineData(new byte[] { 0x80, 0x01 })]
    public void Too_short_chunks_are_rejected(byte[] chunk)
    {
        Assert.Equal(FrameStatus.Rejected, new FrameAssembler().Accept(chunk, 0));
    }

    [Fact]
    public void Random_garbage_never_throws()
    {
        var random = new Random(1234);
        var assembler = new FrameAssembler();
        for (int i = 0; i < 20_000; i++)
        {
            byte[] chunk = new byte[random.Next(0, 250)];
            random.NextBytes(chunk);
            FrameStatus status = assembler.Accept(chunk, i);
            if (status == FrameStatus.Complete)
            {
                byte[] message = assembler.TakeMessage();
                Assert.NotNull(message);
                _ = MessageCodec.Validate(message);
            }
        }
    }
}
