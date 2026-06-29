using System;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins <see cref="DnsQueryPacket"/> — the pure wire-format core behind the Gaming page's DNS benchmark. The
/// live service only opens a UDP socket; the exact query bytes and the rule for what counts as a valid reply
/// are built here. The load-bearing assertions: the query is a well-formed RFC 1035 A-record question, and a
/// reply is accepted ONLY when its transaction id matches and its QR bit is set — so a stray/late datagram can
/// never be counted as the resolver's answer (which would fabricate a latency).
/// </summary>
public class DnsQueryPacketTests
{
    [Fact]
    public void Build_EncodesHeaderQuestion_ExactBytes()
    {
        // domain "a.bc", id 0xABCD → header(RD,QDCOUNT=1) + 01 'a' 02 'b' 'c' 00 + QTYPE A + QCLASS IN.
        byte[] packet = DnsQueryPacket.Build("a.bc", 0xABCD);

        byte[] expected =
        {
            0xAB, 0xCD,             // transaction id
            0x01, 0x00,             // flags: RD=1, QR=0
            0x00, 0x01,             // QDCOUNT = 1
            0x00, 0x00,             // ANCOUNT
            0x00, 0x00,             // NSCOUNT
            0x00, 0x00,             // ARCOUNT
            0x01, (byte)'a',        // label "a"
            0x02, (byte)'b', (byte)'c', // label "bc"
            0x00,                   // root terminator
            0x00, 0x01,             // QTYPE  = A
            0x00, 0x01              // QCLASS = IN
        };
        Assert.Equal(expected, packet);
    }

    [Fact]
    public void Build_MultiLabelDomain_PlacesLabelLengthsAndId()
    {
        byte[] p = DnsQueryPacket.Build("example.com", 0x1234);

        Assert.Equal(0x12, p[0]);
        Assert.Equal(0x34, p[1]);
        Assert.Equal(0x01, p[2]);          // RD set
        Assert.Equal(0x01, p[5]);          // QDCOUNT low byte = 1
        Assert.Equal(7, p[12]);            // length of "example"
        Assert.Equal(3, p[20]);            // length of "com"
        Assert.Equal(0x00, p[24]);         // root terminator
        // ends with QTYPE A + QCLASS IN
        Assert.Equal(new byte[] { 0x00, 0x01, 0x00, 0x01 }, p[^4..]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Build_EmptyDomain_Throws(string? domain)
        => Assert.Throws<ArgumentException>(() => DnsQueryPacket.Build(domain!, 1));

    [Fact]
    public void Build_NonAsciiLabel_Throws_RatherThanCorruptPacket()
        => Assert.Throws<ArgumentException>(() => DnsQueryPacket.Build("café.com", 1));

    [Fact]
    public void Build_OverLongLabel_Throws()
        => Assert.Throws<ArgumentException>(() => DnsQueryPacket.Build(new string('a', 64) + ".com", 1));

    [Fact]
    public void IsValidResponse_AcceptsMatchingIdWithQrSet()
    {
        var response = new byte[12];
        response[0] = 0x12; response[1] = 0x34;
        response[2] = 0x81;   // QR=1, RD=1

        Assert.True(DnsQueryPacket.IsValidResponse(response, 0x1234));
    }

    [Fact]
    public void IsValidResponse_RejectsMismatchedTransactionId()
    {
        var response = new byte[12];
        response[0] = 0x12; response[1] = 0x34; response[2] = 0x81;

        Assert.False(DnsQueryPacket.IsValidResponse(response, 0x9999));
    }

    [Fact]
    public void IsValidResponse_RejectsQueryEcho_WhenQrBitClear()
    {
        var echoed = new byte[12];
        echoed[0] = 0x12; echoed[1] = 0x34;
        echoed[2] = 0x01;   // QR=0 — this is still a query, not a reply

        Assert.False(DnsQueryPacket.IsValidResponse(echoed, 0x1234));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(11)]
    public void IsValidResponse_RejectsTruncatedDatagram(int length)
        => Assert.False(DnsQueryPacket.IsValidResponse(new byte[length], 0x1234));

    [Fact]
    public void IsValidResponse_RejectsNull()
        => Assert.False(DnsQueryPacket.IsValidResponse(null, 1));

    [Fact]
    public void Build_Then_IsValidResponse_RoundTripsTheId()
    {
        // The id the builder writes is exactly the id the validator reads back (with QR flipped on).
        byte[] query = DnsQueryPacket.Build("cloudflare.com", 0x4F2A);
        byte[] reply = (byte[])query.Clone();
        reply[2] |= 0x80;   // resolver sets QR

        Assert.True(DnsQueryPacket.IsValidResponse(reply, 0x4F2A));
    }
}
