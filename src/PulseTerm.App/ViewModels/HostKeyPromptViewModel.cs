using System;
using System.Reactive;
using PulseTerm.Core.Ssh;
using ReactiveUI;

namespace PulseTerm.App.ViewModels;

public class HostKeyPromptViewModel : ReactiveObject
{
    private bool? _result;

    public HostKeyPromptViewModel(
        string host,
        int port,
        string keyType,
        string fingerprint,
        HostKeyVerification verificationResult)
    {
        Host = host;
        Port = port;
        KeyType = keyType;
        Fingerprint = fingerprint;
        VerificationResult = verificationResult;
        IsChanged = verificationResult == HostKeyVerification.Changed;

        TrustCommand = ReactiveCommand.Create(() => { Result = true; });
        RejectCommand = ReactiveCommand.Create(() => { Result = false; });
    }

    public string Host { get; }
    public int Port { get; }
    public string KeyType { get; }
    public string Fingerprint { get; }
    public HostKeyVerification VerificationResult { get; }
    public bool IsChanged { get; }

    public string WarningText => IsChanged
        ? Core.Resources.Strings.HostKeyChanged
        : Core.Resources.Strings.HostKeyUnknown;

    public bool? Result
    {
        get => _result;
        private set => this.RaiseAndSetIfChanged(ref _result, value);
    }

    public ReactiveCommand<Unit, Unit> TrustCommand { get; }
    public ReactiveCommand<Unit, Unit> RejectCommand { get; }
}
