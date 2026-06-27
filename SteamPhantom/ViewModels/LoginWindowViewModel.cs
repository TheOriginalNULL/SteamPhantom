using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamPhantom.Services;

namespace SteamPhantom.ViewModels;

public enum LoginStage
{
    Credentials,
    Connecting,
    Code,
    MobileConfirm
}

public partial class LoginWindowViewModel : ObservableObject
{
    private readonly SteamKitClient _kit;
    private readonly SteamAuthStore _store;
    private readonly UiAuthenticator _authenticator = new();

    private TaskCompletionSource<string>? _codeTcs;

    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private LoginStage _stage = LoginStage.Credentials;
    [ObservableProperty] private string _codePrompt = string.Empty;
    [ObservableProperty] private string _codeInput = string.Empty;

    public bool IsAuthenticated { get; private set; }
    public event Action? RequestClose;

    public LoginWindowViewModel(SteamKitClient kit, SteamAuthStore store)
    {
        _kit = kit;
        _store = store;
        _authenticator.OnEmailCodeRequested =
            (email, wrong) => PromptForCodeAsync(
                wrong
                    ? $"Code was incorrect. Try again — enter the code sent to {email}."
                    : $"Steam sent a code to {email}. Enter it below.");
        _authenticator.OnDeviceCodeRequested =
            (wrong) => PromptForCodeAsync(
                wrong
                    ? "Code was incorrect. Enter the new Steam Guard code from your mobile app."
                    : "Enter the current Steam Guard code from your Steam mobile app.");
        _authenticator.OnDeviceConfirmationRequested = () =>
        {
            Stage = LoginStage.MobileConfirm;
            StatusMessage = "Open the Steam mobile app and approve this sign-in.";
            return Task.FromResult(true);
        };
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            StatusMessage = "Enter your Steam username and password.";
            return;
        }

        Stage = LoginStage.Connecting;
        StatusMessage = "Connecting to Steam...";

        try
        {
            var result = await _kit.LoginWithCredentialsAsync(
                Username.Trim(), Password, _authenticator).ConfigureAwait(true);

            _store.Save(result.AccountName, result.RefreshToken);
            IsAuthenticated = true;
            StatusMessage = string.Empty;
            RequestClose?.Invoke();
        }
        catch (OperationCanceledException)
        {
            Stage = LoginStage.Credentials;
            StatusMessage = "Sign-in cancelled.";
        }
        catch (Exception ex)
        {
            Stage = LoginStage.Credentials;
            StatusMessage = $"Sign-in failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SubmitCode()
    {
        var tcs = _codeTcs;
        _codeTcs = null;
        var code = CodeInput.Trim();
        CodeInput = string.Empty;
        Stage = LoginStage.Connecting;
        StatusMessage = "Verifying code...";
        tcs?.TrySetResult(code);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke();

    private Task<string> PromptForCodeAsync(string prompt)
    {
        CodePrompt = prompt;
        Stage = LoginStage.Code;
        _codeTcs = new TaskCompletionSource<string>();
        return _codeTcs.Task;
    }
}
