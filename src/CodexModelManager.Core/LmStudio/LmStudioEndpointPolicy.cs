namespace CodexModelManager.Core.LmStudio;

public static class LmStudioEndpointPolicy
{
    public static void Validate(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri)
        {
            throw new InvalidOperationException("LM Studio endpoint 必须是绝对 URI。");
        }

        bool isHttp = endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        bool isHttps = endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        if ((!isHttp && !isHttps) || (!endpoint.IsLoopback && !isHttps))
        {
            throw new InvalidOperationException("LM Studio endpoint 必须是 loopback HTTP/HTTPS；非 loopback 只允许 HTTPS。");
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new InvalidOperationException("LM Studio endpoint 不允许在 URI 中嵌入用户名或密码。");
        }

        if (!string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidOperationException("LM Studio endpoint 必须是稳定 base URI，不能包含 query 或 fragment。");
        }
    }
}
