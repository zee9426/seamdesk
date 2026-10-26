using System.Net;

namespace Seamdesk.Transport.Tests;

public class AddressFilterTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("10.20.30.40", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("169.254.7.7", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("febf::1", true)]
    [InlineData("::ffff:192.168.1.20", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.15.255.255", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.169.0.1", false)]
    [InlineData("169.255.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("11.0.0.1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("fe00::1", false)]
    [InlineData("fec0::1", false)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void Only_loopback_private_and_link_local_sources_are_allowed(string address, bool allowed) =>
        Assert.Equal(allowed, AddressFilter.IsAllowed(IPAddress.Parse(address)));
}
