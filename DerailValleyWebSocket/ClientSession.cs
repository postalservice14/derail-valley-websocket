using System.Collections.Generic;

namespace DerailValleyWebSocket;

public class ClientSession
{
    public string Id;
    public HashSet<(string VarName, string Unit)> SubscribedVars = new();
    public HashSet<string> SubscribedEvents = new();

    /// <summary>0 = legacy var/subscribe protocol; 2 = sent Hello, receives State snapshots and may send Commands.</summary>
    public int Protocol;

    public ClientSession(string id)
    {
        Id = id;
    }
}
