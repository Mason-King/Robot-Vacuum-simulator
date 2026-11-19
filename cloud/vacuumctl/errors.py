"""Plain-language readings of AWS and network exceptions (C.2). No botocore import needed."""

CREDENTIAL_ERRORS = ("NoCredentialsError", "PartialCredentialsError", "ProfileNotFound", "TokenRetrievalError",
                     "UnauthorizedSSOTokenError", "SSOTokenLoadError", "CredentialRetrievalError", "SSOError",
                     "InvalidConfigError")
NETWORK_ERRORS = ("EndpointConnectionError", "ConnectTimeoutError", "ReadTimeoutError", "ConnectionClosedError",
                  "ProxyConnectionError", "SSLError", "HTTPClientError")


def error_code(error):
    return (getattr(error, "response", None) or {}).get("Error", {}).get("Code", "")


def plain_error(error, profile=None):
    kind = type(error).__name__
    who = f" --profile {profile}" if profile else ""
    if kind in ("UnauthorizedSSOTokenError", "SSOTokenLoadError", "TokenRetrievalError"):
        return (f"Your AWS SSO sign-in has expired ({kind}). Press \"Sign in (SSO)\" in Network Settings, "
                f"or run `aws sso login{who}`.")
    if kind == "ProfileNotFound":
        return f"There is no AWS profile{who.replace(' --profile', '')} on this computer. Check the profile name, or set it up with `aws configure{who}`."
    if kind in CREDENTIAL_ERRORS:
        return (f"AWS credentials are missing or expired ({kind}). Sign in again with `aws sso login{who}` "
                f"or set them up with `aws configure{who}`.")
    if kind in NETWORK_ERRORS:
        return (f"Could not reach AWS (network problem, {kind}): {error}. Check the internet connection, "
                "and the HTTPS proxy setting if your organisation uses one.")
    if kind == "ClientError":
        details = (getattr(error, "response", None) or {}).get("Error", {})
        code = details.get("Code", "error")
        if code in ("ExpiredToken", "ExpiredTokenException", "RequestExpired"):
            return f"Your AWS session has expired ({code}). Sign in again{' with `aws sso login' + who + '`' if who else ''}."
        if code in ("InvalidClientTokenId", "UnrecognizedClientException", "SignatureDoesNotMatch", "AuthFailure"):
            return f"AWS doesn't accept these credentials ({code}). Check the access keys or sign in again."
        if code in ("OptInRequired",):
            return "This region isn't enabled for the account. Enable it in the AWS console or pick another region."
        return f"AWS refused the request ({code}): {details.get('Message', error)}"
    return f"{kind}: {error}"
