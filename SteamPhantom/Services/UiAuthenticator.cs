using SteamKit2.Authentication;

namespace SteamPhantom.Services;

/// <summary>
/// SteamKit2 IAuthenticator that bridges the synchronous credentials-flow
/// callbacks (need 2FA code / accept on mobile) to UI prompts via TCS.
/// The login window subscribes to these events and resolves the TCS once
/// the user provides input.
/// </summary>
public class UiAuthenticator : IAuthenticator
{
    public delegate Task<string> CodeRequestHandler(bool wasIncorrect);
    public delegate Task<string> EmailCodeRequestHandler(string email, bool wasIncorrect);
    public delegate Task<bool>   ConfirmationHandler();

    public CodeRequestHandler?      OnDeviceCodeRequested;
    public EmailCodeRequestHandler? OnEmailCodeRequested;
    public ConfirmationHandler?     OnDeviceConfirmationRequested;

    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
        => OnDeviceCodeRequested?.Invoke(previousCodeWasIncorrect) ?? Task.FromResult(string.Empty);

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
        => OnEmailCodeRequested?.Invoke(email, previousCodeWasIncorrect) ?? Task.FromResult(string.Empty);

    public Task<bool> AcceptDeviceConfirmationAsync()
        => OnDeviceConfirmationRequested?.Invoke() ?? Task.FromResult(true);
}
