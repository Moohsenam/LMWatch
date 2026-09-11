using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace ClaudeWatch.Core;

public sealed class AdapterInfo
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public bool IsUp { get; init; }
    public bool LooksLikeVpn { get; init; }
    public bool Trusted { get; init; }
    public bool Ignored { get; init; }
    public bool CountsAsVpn { get; init; }
    public long SpeedMbps { get; init; }

    /// <summary>This adapter is the one Windows would send internet traffic out of.</summary>
    public bool CarriesTraffic { get; init; }

    /// <summary>
    /// Looks like a VPN and is up, but your traffic is going somewhere else, so it
    /// is protecting nothing. The state a mesh VPN sits in all day.
    /// </summary>
    public bool IdleTunnel => IsUp && LooksLikeVpn && !Ignored && !CarriesTraffic;

    public string Display => string.IsNullOrWhiteSpace(Description) || Description == Name
        ? Name
        : $"{Name} — {Description}";
}

public sealed class VpnStatus
{
    public bool Connected { get; init; }
    public IReadOnlyList<string> Adapters { get; init; } = Array.Empty<string>();
    public IReadOnlyList<AdapterInfo> AllAdapters { get; init; } = Array.Empty<AdapterInfo>();

    public string Detail => Connected && Adapters.Count > 0 ? string.Join(", ", Adapters) : string.Empty;
}

/// <summary>
/// Decides whether a VPN tunnel is up, from the network adapter table alone.
/// No PowerShell, no WMI query per tick: the whole scan is an in-process call
/// that takes a millisecond or two.
/// </summary>
public sealed class VpnDetector
{
    private Regex? _compiled;
    private string _compiledPattern = string.Empty;

    public VpnStatus Scan(GuardSettings settings)
    {
        var adapters = new List<AdapterInfo>();
        var matched = new List<string>();

        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            return new VpnStatus { Connected = false };
        }

        var regex = GetRegex(settings.VpnAdapterPattern);

        // Which adapter the open internet actually leaves by. Null means the
        // question could not be answered, and an unanswered question must not
        // start closing Claude, so the old name-matching stands in that case.
        var outbound = settings.RequireDefaultRoute ? OutboundAddress() : null;

        foreach (var nic in interfaces)
        {
            string name, description, kind;
            bool up;
            long speed = 0;

            try
            {
                name = nic.Name ?? string.Empty;
                description = nic.Description ?? string.Empty;
                kind = nic.NetworkInterfaceType.ToString();
                up = nic.OperationalStatus == OperationalStatus.Up;
                try { speed = nic.Speed / 1_000_000; } catch { speed = 0; }
            }
            catch
            {
                continue;
            }

            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            var haystack = $"{name} {description}";
            var byName = regex is not null && regex.IsMatch(haystack);
            var byKind = settings.TrustTunnelAdapters &&
                         (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                          nic.NetworkInterfaceType == NetworkInterfaceType.Ppp);

            var trusted = settings.TrustedAdapters.Any(a => Matches(a, name, description));
            var ignored = settings.IgnoredAdapters.Any(a => Matches(a, name, description));

            var looksLikeVpn = byName || byKind || trusted;

            var carries = up && looksLikeVpn && outbound is not null && HasAddress(nic, outbound);

            var counts = Counts(
                up: up,
                looksLikeVpn: looksLikeVpn,
                ignored: ignored,
                trusted: trusted,
                requireRoute: settings.RequireDefaultRoute,
                routeKnown: outbound is not null,
                carriesTraffic: carries);

            adapters.Add(new AdapterInfo
            {
                Name = name,
                Description = description,
                Kind = kind,
                IsUp = up,
                LooksLikeVpn = looksLikeVpn,
                Trusted = trusted,
                Ignored = ignored,
                CountsAsVpn = counts,
                CarriesTraffic = carries,
                SpeedMbps = speed
            });

            if (counts)
            {
                matched.Add(name);
            }
        }

        adapters = adapters
            .OrderByDescending(a => a.CountsAsVpn)
            .ThenByDescending(a => a.IsUp)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new VpnStatus
        {
            Connected = matched.Count > 0,
            Adapters = matched.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            AllAdapters = adapters
        };
    }

    /// <summary>
    /// Whether one adapter should be read as "the VPN is up". Kept pure and
    /// separate from the network scan so every branch can be tested.
    /// </summary>
    public static bool Counts(
        bool up,
        bool looksLikeVpn,
        bool ignored,
        bool trusted,
        bool requireRoute,
        bool routeKnown,
        bool carriesTraffic)
    {
        if (!up || !looksLikeVpn || ignored)
        {
            return false;
        }

        // Marking an adapter as trusted is the user saying "this one is my VPN",
        // which has to win: some corporate clients tunnel without ever owning
        // the default route.
        if (trusted || !requireRoute || !routeKnown)
        {
            return true;
        }

        return carriesTraffic;
    }

    /// <summary>
    /// The local address Windows would use to reach the open internet. Connecting
    /// a UDP socket sends no packets at all; it only makes the OS pick a route,
    /// which is the entire question being asked.
    /// </summary>
    public static IPAddress? OutboundAddress()
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(new IPAddress(new byte[] { 8, 8, 8, 8 }), 53));
            return (probe.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch
        {
            // No route at all, or a locked-down stack. Either way: unknown.
            return null;
        }
    }

    private static bool HasAddress(NetworkInterface nic, IPAddress address)
    {
        try
        {
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.Equals(address))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Adapter went away mid-scan.
        }

        return false;
    }

    private static bool Matches(string candidate, string name, string description)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        return candidate.Equals(name, StringComparison.OrdinalIgnoreCase)
               || candidate.Equals(description, StringComparison.OrdinalIgnoreCase);
    }

    private Regex? GetRegex(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        if (_compiled is not null && _compiledPattern == pattern)
        {
            return _compiled;
        }

        try
        {
            _compiled = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));
            _compiledPattern = pattern;
        }
        catch (ArgumentException)
        {
            _compiled = null;
            _compiledPattern = pattern;
        }

        return _compiled;
    }

    /// <summary>Used by the settings screen to tell the user a pattern is broken.</summary>
    public static bool IsValidRegex(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        try
        {
            _ = new Regex(pattern);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
