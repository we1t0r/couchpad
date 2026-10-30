using System.Net;
using Makaretu.Dns;

namespace RemoteDesk.Agent.Core;

/// <summary>Объявляет агента в локальной сети как _couchpad._tcp, чтобы телефон нашёл его без ввода IP.</summary>
public sealed class MdnsAdvertiser : IDisposable
{
    readonly ServiceDiscovery discovery;
    readonly ServiceProfile profile;

    public MdnsAdvertiser(AgentIdentity identity, int port, IEnumerable<string> addresses)
    {
        profile = new ServiceProfile(
            $"Couchpad-{identity.Id[..8]}",
            Protocol.ServiceType,
            (ushort)port,
            addresses.Select(IPAddress.Parse));
        profile.AddProperty("v", Protocol.Version.ToString());
        profile.AddProperty("id", identity.Id);
        profile.AddProperty("name", identity.Name);
        profile.AddProperty("fp", identity.Fingerprint);

        discovery = new ServiceDiscovery();
        discovery.Advertise(profile);
        discovery.Announce(profile);
    }

    public void Dispose()
    {
        try
        {
            discovery.Unadvertise(profile);
        }
        finally
        {
            discovery.Dispose();
        }
    }
}
