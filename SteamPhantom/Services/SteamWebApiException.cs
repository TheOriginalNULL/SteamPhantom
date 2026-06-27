namespace SteamPhantom.Services;

public enum SteamWebApiFailure
{
    MissingCredentials,
    InvalidApiKey,
    ProfileNotFound,
    PrivateProfile,
    Network,
    Unknown
}

public class SteamWebApiException : Exception
{
    public SteamWebApiFailure Failure { get; }

    public SteamWebApiException(SteamWebApiFailure failure, string message, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
    }
}
