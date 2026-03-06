using System;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;

namespace PulseTerm.App.ViewModels;

public class QuickConnectViewModel : ReactiveObject
{
    private string _input;
    private string _parsedHost;
    private int _parsedPort;
    private string _parsedUsername;

    public QuickConnectViewModel()
    {
        _input = string.Empty;
        _parsedHost = string.Empty;
        _parsedPort = 22;
        _parsedUsername = Environment.UserName;

        this.WhenAnyValue(x => x.Input)
            .Subscribe(ParseInput);

        var canConnect = this.WhenAnyValue(x => x.ParsedHost)
            .Select(host => !string.IsNullOrWhiteSpace(host));

        ConnectCommand = ReactiveCommand.Create(() => { }, canConnect);
    }

    public string Input
    {
        get => _input;
        set => this.RaiseAndSetIfChanged(ref _input, value);
    }

    public string ParsedHost
    {
        get => _parsedHost;
        private set => this.RaiseAndSetIfChanged(ref _parsedHost, value);
    }

    public int ParsedPort
    {
        get => _parsedPort;
        private set => this.RaiseAndSetIfChanged(ref _parsedPort, value);
    }

    public string ParsedUsername
    {
        get => _parsedUsername;
        private set => this.RaiseAndSetIfChanged(ref _parsedUsername, value);
    }

    public ReactiveCommand<Unit, Unit> ConnectCommand { get; }

    private void ParseInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            ParsedHost = string.Empty;
            ParsedPort = 22;
            ParsedUsername = Environment.UserName;
            return;
        }

        var remaining = input.Trim();
        var username = Environment.UserName;
        var port = 22;

        var atIndex = remaining.IndexOf('@');
        if (atIndex >= 0)
        {
            username = remaining.Substring(0, atIndex);
            remaining = remaining.Substring(atIndex + 1);
        }

        var colonIndex = remaining.LastIndexOf(':');
        if (colonIndex >= 0)
        {
            var portStr = remaining.Substring(colonIndex + 1);
            if (int.TryParse(portStr, out var parsedPort) && parsedPort > 0 && parsedPort <= 65535)
            {
                port = parsedPort;
            }
            remaining = remaining.Substring(0, colonIndex);
        }

        ParsedUsername = username;
        ParsedHost = remaining;
        ParsedPort = port;
    }
}
