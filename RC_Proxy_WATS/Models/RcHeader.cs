using System.Text;

namespace RC_Proxy_WATS.Models
{
    public class RcHeader
    {
        public const int HeaderSize = 16;
        
        public string Session { get; set; } = string.Empty;
        public uint SequenceNumber { get; set; }
        public ushort BlockCount { get; set; }

        public static RcHeader FromBytes(byte[] data)
        {
            if (data.Length < HeaderSize)
                throw new ArgumentException("Invalid header data length");

            var header = new RcHeader();
            
            // Session name (10 bytes, null-terminated)
            byte[] sessionBytes = new byte[10];
            Array.Copy(data, 0, sessionBytes, 0, 10);
            header.Session = Encoding.ASCII.GetString(sessionBytes).TrimEnd('\0');
            
            // Sequence number (4 bytes)
            header.SequenceNumber = BitConverter.ToUInt32(data, 10);
            
            // Block count (2 bytes)
            header.BlockCount = BitConverter.ToUInt16(data, 14);
            
            return header;
        }

        public byte[] ToBytes()
        {
            byte[] data = new byte[HeaderSize];
            
            // Session name (10 bytes, null-padded)
            byte[] sessionBytes = new byte[10];
            byte[] sessionData = Encoding.ASCII.GetBytes(Session);
            Array.Copy(sessionData, sessionBytes, Math.Min(sessionData.Length, 10));
            Array.Copy(sessionBytes, 0, data, 0, 10);
            
            // Sequence number (4 bytes)
            BitConverter.GetBytes(SequenceNumber).CopyTo(data, 10);
            
            // Block count (2 bytes)
            BitConverter.GetBytes(BlockCount).CopyTo(data, 14);
            
            return data;
        }
    }

    public class RcMessageBlock
    {
        public const int BlockHeaderSize = 2;
        
        public ushort Length { get; set; }
        public byte[] Payload { get; set; } = Array.Empty<byte>();

        public static RcMessageBlock FromBytes(byte[] data, int offset)
        {
            if (data.Length < offset + BlockHeaderSize)
                throw new ArgumentException("Invalid block data length");

            var block = new RcMessageBlock();
            block.Length = BitConverter.ToUInt16(data, offset);
            
            if (data.Length < offset + BlockHeaderSize + block.Length)
                throw new ArgumentException("Invalid block payload length");
            
            block.Payload = new byte[block.Length];
            Array.Copy(data, offset + BlockHeaderSize, block.Payload, 0, block.Length);
            
            return block;
        }

        public byte[] ToBytes()
        {
            byte[] data = new byte[BlockHeaderSize + Length];
            
            // Length (2 bytes)
            BitConverter.GetBytes(Length).CopyTo(data, 0);
            
            // Payload
            if (Payload.Length > 0)
            {
                Array.Copy(Payload, 0, data, BlockHeaderSize, Math.Min(Payload.Length, Length));
            }
            
            return data;
        }
    }

    public class RcMessage
    {
        public RcHeader Header { get; set; } = new RcHeader();
        public List<RcMessageBlock> Blocks { get; set; } = new List<RcMessageBlock>();

        public static RcMessage FromBytes(byte[] data)
        {
            if (data.Length < RcHeader.HeaderSize)
                throw new ArgumentException("Invalid message data length");

            var message = new RcMessage();
            message.Header = RcHeader.FromBytes(data);
            
            int offset = RcHeader.HeaderSize;
            
            for (int i = 0; i < message.Header.BlockCount; i++)
            {
                var block = RcMessageBlock.FromBytes(data, offset);
                message.Blocks.Add(block);
                offset += RcMessageBlock.BlockHeaderSize + block.Length;
            }
            
            return message;
        }

        public byte[] ToBytes()
        {
            Header.BlockCount = (ushort)Blocks.Count;
            
            var headerBytes = Header.ToBytes();
            var blockBytes = new List<byte[]>();
            
            int totalLength = headerBytes.Length;
            
            foreach (var block in Blocks)
            {
                block.Length = (ushort)block.Payload.Length;
                var blockData = block.ToBytes();
                blockBytes.Add(blockData);
                totalLength += blockData.Length;
            }
            
            byte[] result = new byte[totalLength];
            int offset = 0;
            
            // Header
            Array.Copy(headerBytes, 0, result, offset, headerBytes.Length);
            offset += headerBytes.Length;
            
            // Blocks
            foreach (var blockData in blockBytes)
            {
                Array.Copy(blockData, 0, result, offset, blockData.Length);
                offset += blockData.Length;
            }
            
            return result;
        }

        public bool IsHeartbeat => Header.BlockCount == 0;
        
        public bool IsRewindRequest => 
            Blocks.Count > 0 && 
            Blocks[0].Payload.Length > 0 && 
            Blocks[0].Payload[0] == (byte)'R';
            
        public bool IsCcgMessage => 
            Blocks.Any(b => b.Payload.Length > 0 && b.Payload[0] == (byte)'B');
    }

    public class StoredCcgMessage
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public uint SequenceNumber { get; set; }
        public byte[] RcMessageData { get; set; } = Array.Empty<byte>(); // Store entire RC message
        public string Session { get; set; } = string.Empty;
        
        public static StoredCcgMessage FromRcMessage(RcMessage rcMessage)
        {
            // Verify the message contains CCG data
            if (!rcMessage.IsCcgMessage)
                throw new ArgumentException("Message does not contain CCG data");
            
            return new StoredCcgMessage
            {
                SequenceNumber = rcMessage.Header.SequenceNumber,
                Session = rcMessage.Header.Session,
                RcMessageData = rcMessage.ToBytes() // Store entire RC message as bytes
            };
        }
        
        public RcMessage ToRcMessage()
        {
            // Reconstruct the original RC message from stored bytes
            return RcMessage.FromBytes(RcMessageData);
        }
    }
}