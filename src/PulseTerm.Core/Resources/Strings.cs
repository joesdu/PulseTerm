using System.Globalization;
using System.Resources;

namespace PulseTerm.Core.Resources;

public static class Strings
{
    private static readonly ResourceManager ResourceManager = 
        new ResourceManager("PulseTerm.Core.Resources.Strings", typeof(Strings).Assembly);
    
    public static string AppName => ResourceManager.GetString(nameof(AppName), CultureInfo.CurrentUICulture) ?? nameof(AppName);
    public static string Ready => ResourceManager.GetString(nameof(Ready), CultureInfo.CurrentUICulture) ?? nameof(Ready);
    public static string QuickConnectPlaceholder => ResourceManager.GetString(nameof(QuickConnectPlaceholder), CultureInfo.CurrentUICulture) ?? nameof(QuickConnectPlaceholder);
    public static string QuickConnect => ResourceManager.GetString(nameof(QuickConnect), CultureInfo.CurrentUICulture) ?? nameof(QuickConnect);
    public static string RecentConnections => ResourceManager.GetString(nameof(RecentConnections), CultureInfo.CurrentUICulture) ?? nameof(RecentConnections);
    public static string ServerGroups => ResourceManager.GetString(nameof(ServerGroups), CultureInfo.CurrentUICulture) ?? nameof(ServerGroups);
    public static string Settings => ResourceManager.GetString(nameof(Settings), CultureInfo.CurrentUICulture) ?? nameof(Settings);
    public static string Notifications => ResourceManager.GetString(nameof(Notifications), CultureInfo.CurrentUICulture) ?? nameof(Notifications);
    
    public static string NewTab => ResourceManager.GetString(nameof(NewTab), CultureInfo.CurrentUICulture) ?? nameof(NewTab);
    public static string CloseTab => ResourceManager.GetString(nameof(CloseTab), CultureInfo.CurrentUICulture) ?? nameof(CloseTab);
    public static string Search => ResourceManager.GetString(nameof(Search), CultureInfo.CurrentUICulture) ?? nameof(Search);
    public static string Copy => ResourceManager.GetString(nameof(Copy), CultureInfo.CurrentUICulture) ?? nameof(Copy);
    public static string Split => ResourceManager.GetString(nameof(Split), CultureInfo.CurrentUICulture) ?? nameof(Split);
    public static string Broadcast => ResourceManager.GetString(nameof(Broadcast), CultureInfo.CurrentUICulture) ?? nameof(Broadcast);
    public static string SyncGroup => ResourceManager.GetString(nameof(SyncGroup), CultureInfo.CurrentUICulture) ?? nameof(SyncGroup);
    
    public static string FileName => ResourceManager.GetString(nameof(FileName), CultureInfo.CurrentUICulture) ?? nameof(FileName);
    public static string Size => ResourceManager.GetString(nameof(Size), CultureInfo.CurrentUICulture) ?? nameof(Size);
    public static string Permissions => ResourceManager.GetString(nameof(Permissions), CultureInfo.CurrentUICulture) ?? nameof(Permissions);
    public static string Modified => ResourceManager.GetString(nameof(Modified), CultureInfo.CurrentUICulture) ?? nameof(Modified);
    public static string Upload => ResourceManager.GetString(nameof(Upload), CultureInfo.CurrentUICulture) ?? nameof(Upload);
    public static string Download => ResourceManager.GetString(nameof(Download), CultureInfo.CurrentUICulture) ?? nameof(Download);
    public static string Refresh => ResourceManager.GetString(nameof(Refresh), CultureInfo.CurrentUICulture) ?? nameof(Refresh);
    
    public static string LocalForward => ResourceManager.GetString(nameof(LocalForward), CultureInfo.CurrentUICulture) ?? nameof(LocalForward);
    public static string RemoteForward => ResourceManager.GetString(nameof(RemoteForward), CultureInfo.CurrentUICulture) ?? nameof(RemoteForward);
    public static string LocalPort => ResourceManager.GetString(nameof(LocalPort), CultureInfo.CurrentUICulture) ?? nameof(LocalPort);
    public static string RemoteAddress => ResourceManager.GetString(nameof(RemoteAddress), CultureInfo.CurrentUICulture) ?? nameof(RemoteAddress);
    public static string NewTunnel => ResourceManager.GetString(nameof(NewTunnel), CultureInfo.CurrentUICulture) ?? nameof(NewTunnel);
    public static string ActiveTunnels => ResourceManager.GetString(nameof(ActiveTunnels), CultureInfo.CurrentUICulture) ?? nameof(ActiveTunnels);
    
    public static string SearchCommands => ResourceManager.GetString(nameof(SearchCommands), CultureInfo.CurrentUICulture) ?? nameof(SearchCommands);
    public static string SystemMonitor => ResourceManager.GetString(nameof(SystemMonitor), CultureInfo.CurrentUICulture) ?? nameof(SystemMonitor);
    public static string Network => ResourceManager.GetString(nameof(Network), CultureInfo.CurrentUICulture) ?? nameof(Network);
    public static string Docker => ResourceManager.GetString(nameof(Docker), CultureInfo.CurrentUICulture) ?? nameof(Docker);
    public static string Custom => ResourceManager.GetString(nameof(Custom), CultureInfo.CurrentUICulture) ?? nameof(Custom);
    public static string QuickCommands => ResourceManager.GetString(nameof(QuickCommands), CultureInfo.CurrentUICulture) ?? nameof(QuickCommands);
    public static string AddCommand => ResourceManager.GetString(nameof(AddCommand), CultureInfo.CurrentUICulture) ?? nameof(AddCommand);
    public static string Description => ResourceManager.GetString(nameof(Description), CultureInfo.CurrentUICulture) ?? nameof(Description);
    public static string Command => ResourceManager.GetString(nameof(Command), CultureInfo.CurrentUICulture) ?? nameof(Command);
    public static string Category => ResourceManager.GetString(nameof(Category), CultureInfo.CurrentUICulture) ?? nameof(Category);
    public static string System => ResourceManager.GetString(nameof(System), CultureInfo.CurrentUICulture) ?? nameof(System);
    
    public static string Connected => ResourceManager.GetString(nameof(Connected), CultureInfo.CurrentUICulture) ?? nameof(Connected);
    public static string Connecting => ResourceManager.GetString(nameof(Connecting), CultureInfo.CurrentUICulture) ?? nameof(Connecting);
    public static string Disconnected => ResourceManager.GetString(nameof(Disconnected), CultureInfo.CurrentUICulture) ?? nameof(Disconnected);
    public static string Latency => ResourceManager.GetString(nameof(Latency), CultureInfo.CurrentUICulture) ?? nameof(Latency);
    
    public static string Sessions => ResourceManager.GetString(nameof(Sessions), CultureInfo.CurrentUICulture) ?? nameof(Sessions);
    public static string NoSavedSessions => ResourceManager.GetString(nameof(NoSavedSessions), CultureInfo.CurrentUICulture) ?? nameof(NoSavedSessions);
    public static string MoveToGroup => ResourceManager.GetString(nameof(MoveToGroup), CultureInfo.CurrentUICulture) ?? nameof(MoveToGroup);
    public static string ClearAll => ResourceManager.GetString(nameof(ClearAll), CultureInfo.CurrentUICulture) ?? nameof(ClearAll);
    
    public static string Connect => ResourceManager.GetString(nameof(Connect), CultureInfo.CurrentUICulture) ?? nameof(Connect);
    public static string Disconnect => ResourceManager.GetString(nameof(Disconnect), CultureInfo.CurrentUICulture) ?? nameof(Disconnect);
    public static string Save => ResourceManager.GetString(nameof(Save), CultureInfo.CurrentUICulture) ?? nameof(Save);
    public static string Cancel => ResourceManager.GetString(nameof(Cancel), CultureInfo.CurrentUICulture) ?? nameof(Cancel);
    public static string Delete => ResourceManager.GetString(nameof(Delete), CultureInfo.CurrentUICulture) ?? nameof(Delete);
    public static string Edit => ResourceManager.GetString(nameof(Edit), CultureInfo.CurrentUICulture) ?? nameof(Edit);
    public static string OK => ResourceManager.GetString(nameof(OK), CultureInfo.CurrentUICulture) ?? nameof(OK);
    public static string Error => ResourceManager.GetString(nameof(Error), CultureInfo.CurrentUICulture) ?? nameof(Error);
    public static string Warning => ResourceManager.GetString(nameof(Warning), CultureInfo.CurrentUICulture) ?? nameof(Warning);
    
    public static string Language => ResourceManager.GetString(nameof(Language), CultureInfo.CurrentUICulture) ?? nameof(Language);
    public static string Theme => ResourceManager.GetString(nameof(Theme), CultureInfo.CurrentUICulture) ?? nameof(Theme);
    public static string Font => ResourceManager.GetString(nameof(Font), CultureInfo.CurrentUICulture) ?? nameof(Font);
    public static string FontSize => ResourceManager.GetString(nameof(FontSize), CultureInfo.CurrentUICulture) ?? nameof(FontSize);
    public static string ScrollbackLines => ResourceManager.GetString(nameof(ScrollbackLines), CultureInfo.CurrentUICulture) ?? nameof(ScrollbackLines);
    
    public static string Password => ResourceManager.GetString(nameof(Password), CultureInfo.CurrentUICulture) ?? nameof(Password);
    public static string PrivateKey => ResourceManager.GetString(nameof(PrivateKey), CultureInfo.CurrentUICulture) ?? nameof(PrivateKey);
    public static string Username => ResourceManager.GetString(nameof(Username), CultureInfo.CurrentUICulture) ?? nameof(Username);
    public static string Host => ResourceManager.GetString(nameof(Host), CultureInfo.CurrentUICulture) ?? nameof(Host);
    public static string Port => ResourceManager.GetString(nameof(Port), CultureInfo.CurrentUICulture) ?? nameof(Port);
    public static string HostKeyVerification => ResourceManager.GetString(nameof(HostKeyVerification), CultureInfo.CurrentUICulture) ?? nameof(HostKeyVerification);
    public static string TrustThisHost => ResourceManager.GetString(nameof(TrustThisHost), CultureInfo.CurrentUICulture) ?? nameof(TrustThisHost);
    
    public static string TestConnection => ResourceManager.GetString(nameof(TestConnection), CultureInfo.CurrentUICulture) ?? nameof(TestConnection);
    public static string BrowseKeyFile => ResourceManager.GetString(nameof(BrowseKeyFile), CultureInfo.CurrentUICulture) ?? nameof(BrowseKeyFile);
    public static string ConnectionProfile => ResourceManager.GetString(nameof(ConnectionProfile), CultureInfo.CurrentUICulture) ?? nameof(ConnectionProfile);
    public static string AuthMethodLabel => ResourceManager.GetString(nameof(AuthMethodLabel), CultureInfo.CurrentUICulture) ?? nameof(AuthMethodLabel);
    public static string DefaultPort => ResourceManager.GetString(nameof(DefaultPort), CultureInfo.CurrentUICulture) ?? nameof(DefaultPort);
    public static string HostKeyChanged => ResourceManager.GetString(nameof(HostKeyChanged), CultureInfo.CurrentUICulture) ?? nameof(HostKeyChanged);
    public static string HostKeyUnknown => ResourceManager.GetString(nameof(HostKeyUnknown), CultureInfo.CurrentUICulture) ?? nameof(HostKeyUnknown);
    public static string Fingerprint => ResourceManager.GetString(nameof(Fingerprint), CultureInfo.CurrentUICulture) ?? nameof(Fingerprint);
    public static string KeyType => ResourceManager.GetString(nameof(KeyType), CultureInfo.CurrentUICulture) ?? nameof(KeyType);
    public static string Trust => ResourceManager.GetString(nameof(Trust), CultureInfo.CurrentUICulture) ?? nameof(Trust);
    public static string Reject => ResourceManager.GetString(nameof(Reject), CultureInfo.CurrentUICulture) ?? nameof(Reject);
    public static string Name => ResourceManager.GetString(nameof(Name), CultureInfo.CurrentUICulture) ?? nameof(Name);
    public static string Group => ResourceManager.GetString(nameof(Group), CultureInfo.CurrentUICulture) ?? nameof(Group);
    public static string Passphrase => ResourceManager.GetString(nameof(Passphrase), CultureInfo.CurrentUICulture) ?? nameof(Passphrase);
}
