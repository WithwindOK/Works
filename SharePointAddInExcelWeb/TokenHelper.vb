Imports System.Collections.ObjectModel
Imports System.Globalization
Imports System.IdentityModel.Tokens
Imports System.IdentityModel.Tokens.Jwt
Imports System.IO
Imports System.Net
Imports System.Security.Claims
Imports System.Security.Cryptography.X509Certificates
Imports System.Security.Principal
Imports System.ServiceModel
Imports System.Web.Configuration
Imports System.Web.Script.Serialization
Imports Microsoft.SharePoint.Client
Imports Microsoft.SharePoint.Client.EventReceivers
Imports SigningCredentials = Microsoft.IdentityModel.Tokens.SigningCredentials
Imports SymmetricSecurityKey = Microsoft.IdentityModel.Tokens.SymmetricSecurityKey
Imports TokenValidationParameters = Microsoft.IdentityModel.Tokens.TokenValidationParameters
Imports X509SigningCredentials = Microsoft.IdentityModel.Tokens.X509SigningCredentials

Public NotInheritable Class TokenHelper

#Region "公共字段"

    ''' <summary>
    ''' SharePoint 主体。
    ''' </summary>
    Public Const SharePointPrincipal As String = "00000003-0000-0ff1-ce00-000000000000"

    ''' <summary>
    ''' HighTrust 访问令牌的生存期(12 小时)。
    ''' </summary>
    Public Shared ReadOnly HighTrustAccessTokenLifetime As TimeSpan = TimeSpan.FromHours(12.0)

#End Region

#Region "公共方法"

    ''' <summary>
    '''通过查找已知参数名称，从指定请求中检索上下文标记字符串
    '''从而从指定请求中检索上下文标记字符串。 如果未找到上下文标记，则返回无。
    ''' </summary>
    ''' <param name="request">要从中查找上下文标记的 HttpRequest</param>
    ''' <returns>上下文标记字符串</returns>
    Public Shared Function GetContextTokenFromRequest(request As HttpRequest) As String
        Return GetContextTokenFromRequest(New HttpRequestWrapper(request))
    End Function

    ''' <summary>
    '''通过查找已知参数名称，从指定请求中检索上下文标记字符串
    '''从而从指定请求中检索上下文标记字符串。 如果未找到上下文标记，则返回无。
    ''' </summary>
    ''' <param name="request">要从中查找上下文标记的 HttpRequest</param>
    ''' <returns>上下文标记字符串</returns>
    Public Shared Function GetContextTokenFromRequest(request As HttpRequestBase) As String
        Dim paramNames As String() = {"AppContext", "AppContextToken", "AccessToken", "SPAppToken"}
        For Each paramName As String In paramNames
            If Not String.IsNullOrEmpty(request.Form(paramName)) Then
                Return request.Form(paramName)
            End If
            If Not String.IsNullOrEmpty(request.QueryString(paramName)) Then
                Return request.QueryString(paramName)
            End If
        Next
        Return Nothing
    End Function

    ''' <summary>
    ''' 基于参数验证指定的上下文标识字符串用于此应用程序
    '''在 web.config 中指定。web.config 中使用的参数用于验证，包括 ClientId，
    '''HostedAppHostNameOverride、HostedAppHostName、ClientSecret 和 Realm (如果指定它)。如果存在 HostedAppHostNameOverride，
    '''则使用其进行验证。否则，如果 <paramref name="appHostName"/> 不是
    '''如果为 Nothing，则使用它而不是 web.config 的 HostedAppHostName 进行验证。如果标记无效，则
    '''引发异常。如果标记有效，则根据标记内容更新 TokenHelper 的静态 STS 元数据 URL，
    '''并且返回基于上下文标记的 JwtSecurityToken。
    ''' </summary>
    ''' <param name="contextTokenString">要验证的上下文标记</param>
    ''' <param name="appHostName">URL 颁发机构，包含域名系统 (DNS) 主机名或 IP 地址及端口号，用于标记的访问群体验证。
    '''如果为 Nothing，则改用 HostedAppHostName web.config 设置。如果存在 HostedAppHostNameOverride web.config 设置，则将使用该设置
    '''代替 <paramref name="appHostName"/> 进行验证。</param>
    ''' <returns>基于上下文标记的 JwtSecurityToken。</returns>
    Public Shared Function ReadAndValidateContextToken(contextTokenString As String, Optional appHostName As String = Nothing) As SharePointContextToken
        Dim securityKeys As List(Of SymmetricSecurityKey) = New List(Of SymmetricSecurityKey) From {
            New SymmetricSecurityKey(Convert.FromBase64String(ClientSecret))
        }

        If Not String.IsNullOrEmpty(SecondaryClientSecret) Then
            securityKeys.Add(New SymmetricSecurityKey(Convert.FromBase64String(SecondaryClientSecret)))
        End If

        Dim tokenHandler As JwtSecurityTokenHandler = CreateJwtSecurityTokenHandler()
        Dim parameters As TokenValidationParameters = New TokenValidationParameters With {
            .ValidateIssuer = False,
            .ValidateAudience = False, ' 以下已验证
            .IssuerSigningKeys = securityKeys ' 验证签名
        }

        Dim securityToken As Microsoft.IdentityModel.Tokens.SecurityToken = Nothing
        tokenHandler.ValidateToken(contextTokenString, parameters, securityToken)
        Dim token As SharePointContextToken = SharePointContextToken.Create(securityToken)

        Dim stsAuthority As String = (New Uri(token.SecurityTokenServiceUri)).Authority
        Dim firstDot As Integer = stsAuthority.IndexOf("."c)

        GlobalEndPointPrefix = stsAuthority.Substring(0, firstDot)
        AcsHostUrl = stsAuthority.Substring(firstDot + 1)


        Dim acceptableAudiences As String()
        If Not [String].IsNullOrEmpty(HostedAppHostNameOverride) Then
            acceptableAudiences = HostedAppHostNameOverride.Split(";"c)
        ElseIf appHostName Is Nothing Then
            acceptableAudiences = {HostedAppHostName}
        Else
            acceptableAudiences = {appHostName}
        End If

        Dim validationSuccessful As Boolean
        Dim definedRealm As String = If(Realm, token.Realm)
        For Each audience In acceptableAudiences
            Dim principal As String = GetFormattedPrincipal(ClientId, audience, definedRealm)
            If token.Audiences.First(Function(item) StringComparer.OrdinalIgnoreCase.Equals(item, principal)) IsNot Nothing Then
                validationSuccessful = True
                Exit For
            End If
        Next

        If Not validationSuccessful Then
            Throw New AudienceUriValidationFailedException([String].Format(CultureInfo.CurrentCulture, """{0}"" is not the intended audience ""{1}""", [String].Join(";", acceptableAudiences), token.Audiences.ToArray))
        End If

        Return token
    End Function

    ''' <summary>
    ''' 从 ACS 检索访问令牌，以在指定 targetHost 中调用指定上下文标记 
    ''' 的源。 必须为发送上下文标记的主体注册 targetHost。
    ''' </summary>
    ''' <param name="contextToken">由预期的访问令牌群体颁发的上下文标记</param>
    ''' <param name="targetHost">的目标主体名称</param>
    ''' <returns>带有与上下文标记源匹配的访问群体的访问令牌</returns>
    Public Shared Function GetAccessToken(contextToken As SharePointContextToken, targetHost As String) As OAuthTokenResponse

        Dim targetPrincipalName As String = contextToken.TargetPrincipalName

        ' 从上下文标记提取 refreshToken
        Dim refreshToken As String = contextToken.RefreshToken

        If [String].IsNullOrEmpty(refreshToken) Then
            Return Nothing
        End If

        Dim targetRealm As String = If(Realm, contextToken.Realm)

        Return GetAccessToken(refreshToken, targetPrincipalName, targetHost, targetRealm)
    End Function

    ''' <summary>
    ''' 使用指定的授权代码从 ACS 检索访问令牌，以调用指定主体
    '''在指定的 targetHost。必须为目标主体注册 targetHost。如果指定的领域为 
    ''' 无，则改用 web.config 中的 "Realm" 设置。
    ''' </summary>
    ''' <param name="authorizationCode">用于交换访问令牌的授权代码</param>
    ''' <param name="targetPrincipalName">要检索目标主体 URL 颁发机构的访问令牌</param>
    ''' <param name="targetHost">的目标主体名称</param>
    ''' <param name="targetRealm">用于访问令牌的 nameid 和访问群体的 Realm</param>
    ''' <param name="redirectUri">已为此外接程序注册重定向 URI</param>
    ''' <returns>带有目标主体访问群体的访问令牌</returns>
    Public Shared Function GetAccessToken(authorizationCode As String, targetPrincipalName As String, targetHost As String, targetRealm As String, redirectUri As Uri) As OAuthTokenResponse

        If targetRealm Is Nothing Then
            targetRealm = Realm
        End If

        Dim resource As String = GetFormattedPrincipal(targetPrincipalName, targetHost, targetRealm)
        Dim formattedClientId As String = GetFormattedPrincipal(ClientId, Nothing, targetRealm)
        Dim acsUri As String = AcsMetadataParser.GetStsUrl(targetRealm)
        Dim oauthResponse As OAuthTokenResponse = Nothing

        Try
            oauthResponse = OAuthClient.GetAccessTokenWithAuthorizationCode(acsUri, formattedClientId, ClientSecret, authorizationCode, redirectUri.AbsoluteUri, resource)

        Catch ex As WebException
            If Not [String].IsNullOrEmpty(SecondaryClientSecret) Then
                oauthResponse = OAuthClient.GetAccessTokenWithAuthorizationCode(acsUri, formattedClientId, SecondaryClientSecret, authorizationCode, redirectUri.AbsoluteUri, resource)
            Else
                Using sr As New StreamReader(ex.Response.GetResponseStream())
                    Dim responseText As String = sr.ReadToEnd()
                    Throw New WebException(ex.Message + " - " + responseText, ex)
                End Using
            End If
        End Try

        Return oauthResponse
    End Function

    ''' <summary>
    ''' 使用指定的刷新标记从 ACS 检索访问令牌，以调用指定主体
    '''在指定的 targetHost。必须为目标主体注册 targetHost。如果指定的领域为 
    ''' 无，则改用 web.config 中的 "Realm" 设置。
    ''' </summary>
    ''' <param name="refreshToken">用于交换访问令牌的刷新标记</param>
    ''' <param name="targetPrincipalName">要检索目标主体 URL 颁发机构的访问令牌</param>
    ''' <param name="targetHost">的目标主体名称</param>
    ''' <param name="targetRealm">用于访问令牌的 nameid 和访问群体的 Realm</param>
    ''' <returns>带有目标主体访问群体的访问令牌</returns>
    Public Shared Function GetAccessToken(refreshToken As String, targetPrincipalName As String, targetHost As String, targetRealm As String) As OAuthTokenResponse

        If targetRealm Is Nothing Then
            targetRealm = Realm
        End If

        Dim resource As String = GetFormattedPrincipal(targetPrincipalName, targetHost, targetRealm)
        Dim formattedClientId As String = GetFormattedPrincipal(ClientId, Nothing, targetRealm)
        Dim acsUri As String = AcsMetadataParser.GetStsUrl(targetRealm)
        Dim oauthResponse As OAuthTokenResponse = Nothing

        Try
            oauthResponse = OAuthClient.GetAccessTokenWithRefreshToken(acsUri, formattedClientId, ClientSecret, refreshToken, resource)

        Catch ex As WebException
            If Not [String].IsNullOrEmpty(SecondaryClientSecret) Then
                oauthResponse = OAuthClient.GetAccessTokenWithRefreshToken(acsUri, formattedClientId, SecondaryClientSecret, refreshToken, resource)
            Else
                Using sr As New StreamReader(ex.Response.GetResponseStream())
                    Dim responseText As String = sr.ReadToEnd()
                    Throw New WebException(ex.Message + " - " + responseText, ex)
                End Using
            End If
        End Try

        Return oauthResponse
    End Function

    ''' <summary>
    ''' 从 ACS 检索只允许应用程序使用的访问令牌，以调用指定主体
    '''在指定的 targetHost。必须为目标主体注册 targetHost。如果指定的领域为 
    ''' 无，则改用 web.config 中的 "Realm" 设置。
    ''' </summary>
    ''' <param name="targetPrincipalName">要检索目标主体 URL 颁发机构的访问令牌</param>
    ''' <param name="targetHost">的目标主体名称</param>
    ''' <param name="targetRealm">用于访问令牌的 nameid 和访问群体的 Realm</param>
    ''' <returns>带有目标主体访问群体的访问令牌</returns>
    Public Shared Function GetAppOnlyAccessToken(targetPrincipalName As String, targetHost As String, targetRealm As String) As OAuthTokenResponse

        If targetRealm Is Nothing Then
            targetRealm = Realm
        End If

        Dim resource As String = GetFormattedPrincipal(targetPrincipalName, targetHost, targetRealm)
        Dim formattedClientId As String = GetFormattedPrincipal(ClientId, HostedAppHostName, targetRealm)
        Dim acsUri As String = AcsMetadataParser.GetStsUrl(targetRealm)
        Dim oauthResponse As OAuthTokenResponse = Nothing

        Try
            oauthResponse = OAuthClient.GetAccessTokenWithClientCredentials(acsUri, formattedClientId, ClientSecret, resource)

        Catch ex As WebException
            If Not [String].IsNullOrEmpty(SecondaryClientSecret) Then
                oauthResponse = OAuthClient.GetAccessTokenWithClientCredentials(acsUri, formattedClientId, SecondaryClientSecret, resource)
            Else
                Using sr As New StreamReader(ex.Response.GetResponseStream())
                    Dim responseText As String = sr.ReadToEnd()
                    Throw New WebException(ex.Message + " - " + responseText, ex)
                End Using
            End If
        End Try

        Return oauthResponse
    End Function

    ''' <summary>
    ''' 根据远程事件接收器的属性创建客户端上下文
    ''' </summary>
    ''' <param name="properties">远程事件接收器的属性</param>
    ''' <returns>ClientContext 准备调用发起事件的 Web</returns>
    Public Shared Function CreateRemoteEventReceiverClientContext(properties As SPRemoteEventProperties) As ClientContext
        Dim sharepointUrl As Uri
        If properties.ListEventProperties IsNot Nothing Then
            sharepointUrl = New Uri(properties.ListEventProperties.WebUrl)
        ElseIf properties.ItemEventProperties IsNot Nothing Then
            sharepointUrl = New Uri(properties.ItemEventProperties.WebUrl)
        ElseIf properties.WebEventProperties IsNot Nothing Then
            sharepointUrl = New Uri(properties.WebEventProperties.FullUrl)
        Else
            Return Nothing
        End If

        If IsHighTrustApp() Then
            Return GetS2SClientContextWithWindowsIdentity(sharepointUrl, Nothing)
        End If

        Return CreateAcsClientContextForUrl(properties, sharepointUrl)

    End Function

    ''' <summary>
    ''' 基于外接程序事件的属性创建客户端上下文
    ''' </summary>
    ''' <param name="properties">外接程序事件的属性</param>
    ''' <param name="useAppWeb">如果定位到应用程序 Web，则为 true；如果定位到主机 Web，则为 false</param>
    ''' <returns>ClientContext 准备调用应用程序 Web 或父网站</returns>
    Public Shared Function CreateAppEventClientContext(properties As SPRemoteEventProperties, useAppWeb As Boolean) As ClientContext
        If properties.AppEventProperties Is Nothing Then
            Return Nothing
        End If

        Dim sharepointUrl As Uri = If(useAppWeb, properties.AppEventProperties.AppWebFullUrl, properties.AppEventProperties.HostWebFullUrl)
        If IsHighTrustApp() Then
            Return GetS2SClientContextWithWindowsIdentity(sharepointUrl, Nothing)
        End If

        Return CreateAcsClientContextForUrl(properties, sharepointUrl)
    End Function

    ''' <summary>
    ''' 使用指定的授权代码从 ACS 检索访问令牌，并使用该访问令牌 
    ''' 创建客户端上下文
    ''' </summary>
    ''' <param name="targetUrl">目标 SharePoint 网站的 URL</param>
    ''' <param name="authorizationCode">从 ACS 检索访问令牌时使用的授权代码</param>
    ''' <param name="redirectUri">已为此外接程序注册重定向 URI</param>
    ''' <returns>ClientContext 准备使用有效访问令牌调用 targetUrl</returns>
    Public Shared Function GetClientContextWithAuthorizationCode(targetUrl As String, authorizationCode As String, redirectUri As Uri) As ClientContext
        Return GetClientContextWithAuthorizationCode(targetUrl, SharePointPrincipal, authorizationCode, GetRealmFromTargetUrl(New Uri(targetUrl)), redirectUri)
    End Function

    ''' <summary>
    ''' 使用指定的授权代码从 ACS 检索访问令牌，并使用该访问令牌 
    ''' 创建客户端上下文
    ''' </summary>
    ''' <param name="targetUrl">目标 SharePoint 网站的 URL</param>
    ''' <param name="targetPrincipalName">目标 SharePoint 主体的名称</param>
    ''' <param name="authorizationCode">从 ACS 检索访问令牌时使用的授权代码</param>
    ''' <param name="targetRealm">用于访问令牌的 nameid 和访问群体的 Realm</param>
    ''' <param name="redirectUri">已为此外接程序注册重定向 URI</param>
    ''' <returns>ClientContext 准备使用有效访问令牌调用 targetUrl</returns>
    Public Shared Function GetClientContextWithAuthorizationCode(targetUrl As String, targetPrincipalName As String, authorizationCode As String, targetRealm As String, redirectUri As Uri) As ClientContext
        Dim targetUri As New Uri(targetUrl)

        Dim accessToken As String = GetAccessToken(authorizationCode, targetPrincipalName, targetUri.Authority, targetRealm, redirectUri).AccessToken

        Return GetClientContextWithAccessToken(targetUrl, accessToken)
    End Function

    ''' <summary>
    ''' 使用指定的访问令牌创建客户端上下文
    ''' </summary>
    ''' <param name="targetUrl">目标 SharePoint 网站的 URL</param>
    ''' <param name="accessToken">调用指定 targetUrl 时使用的访问令牌</param>
    ''' <returns>ClientContext 准备使用指定访问令牌调用 targetUrl</returns>
    Public Shared Function GetClientContextWithAccessToken(targetUrl As String, accessToken As String) As ClientContext
        Dim clientContext As New ClientContext(targetUrl)

        clientContext.AuthenticationMode = ClientAuthenticationMode.Anonymous
        clientContext.FormDigestHandlingEnabled = False

        AddHandler clientContext.ExecutingWebRequest, Sub(oSender As Object, webRequestEventArgs As WebRequestEventArgs)
                                                          webRequestEventArgs.WebRequestExecutor.RequestHeaders("Authorization") = "Bearer " & accessToken
                                                      End Sub
        Return clientContext
    End Function

    ''' <summary>
    ''' 使用指定的上下文标记从 ACS 检索访问令牌，并使用该令牌创建
    ''' 客户端上下文
    ''' </summary>
    ''' <param name="targetUrl">目标 SharePoint 网站的 URL</param>
    ''' <param name="contextTokenString">从目标 SharePoint 网站接收的上下文标记</param>
    ''' <param name="appHostUrl">托管外接程序的 URL 授权。如果它为 Nothing，则改用 HostedAppHostName 中的值
    ''' 中 HostedAppHostName 的值</param>
    ''' <returns>ClientContext 准备使用有效访问令牌调用 targetUrl</returns>
    Public Shared Function GetClientContextWithContextToken(targetUrl As String, contextTokenString As String, appHostUrl As String) As ClientContext
        Dim contextToken As SharePointContextToken = ReadAndValidateContextToken(contextTokenString, appHostUrl)

        Dim targetUri As New Uri(targetUrl)

        Dim accessToken As String = GetAccessToken(contextToken, targetUri.Authority).AccessToken

        Return GetClientContextWithAccessToken(targetUrl, accessToken)
    End Function

    ''' <summary>
    ''' 返回一个 SharePoint URL，外接程序应将浏览器重定向到该 URL，以请求许可并返回
    '''授权代码。
    ''' </summary>
    ''' <param name="contextUrl">SharePoint 网站的绝对 URL</param>
    ''' <param name="scope">以“速记”格式从 SharePoint 网站进行请求的空格分隔权限
    ''' (例如 "Web.Read Site.Write")</param>
    ''' <returns>SharePoint 网站 OAuth 授权页面的 URL</returns>
    Public Shared Function GetAuthorizationUrl(contextUrl As String, scope As String) As String
        Return String.Format("{0}{1}?IsDlg=1&client_id={2}&scope={3}&response_type=code", EnsureTrailingSlash(contextUrl), AuthorizationPage, ClientId, scope)
    End Function

    ''' <summary>
    ''' 返回一个 SharePoint URL，外接程序应将浏览器重定向到该 URL，以请求许可并返回
    '''授权代码。
    ''' </summary>
    ''' <param name="contextUrl">SharePoint 网站的绝对 URL</param>
    ''' <param name="scope">以“速记”格式从 SharePoint 网站进行请求的空格分隔权限
    ''' (例如 "Web.Read Site.Write")</param>
    ''' <param name="redirectUri">在获得同意后，SharePoint 应将浏览器重定向到的 URI
    '''已授予</param>
    ''' <returns>SharePoint 网站 OAuth 授权页面的 URL</returns>
    Public Shared Function GetAuthorizationUrl(contextUrl As String, scope As String, redirectUri As String) As String
        Return String.Format("{0}{1}?IsDlg=1&client_id={2}&scope={3}&response_type=code&redirect_uri={4}", EnsureTrailingSlash(contextUrl), AuthorizationPage, ClientId, scope, redirectUri)
    End Function

    ''' <summary>
    ''' 返回一个 SharePoint URL，外接程序应将浏览器重定向到该 URL，以请求新的上下文标记。
    ''' </summary>
    ''' <param name="contextUrl">SharePoint 网站的绝对 URL</param>
    ''' <param name="redirectUri">SharePoint 应使用上下文标记将浏览器重定向到的 URL</param>
    ''' <returns>SharePoint 网站的上下文标记重定向页面的 URL</returns>
    Public Shared Function GetAppContextTokenRequestUrl(contextUrl As String, redirectUri As String) As String
        Return String.Format("{0}{1}?client_id={2}&redirect_uri={3}", EnsureTrailingSlash(contextUrl), RedirectPage, ClientId, redirectUri)
    End Function

    ''' <summary>
    '''检索由应用程序的专有证书签名的 S2S 访问令牌
    ''' WindowsIdentity 并用于 targetApplicationUri 处的 SharePoint。如果未指定领域
    ''' Realm，将向 targetApplicationUri 发出身份验证质询以发现它。
    ''' </summary>
    ''' <param name="targetApplicationUri">目标 SharePoint 网站的 URL</param>
    ''' <param name="identity">代表用户创建访问令牌的 Windows 标识</param>
    ''' <returns>带有目标主体访问群体的访问令牌</returns>
    Public Shared Function GetS2SAccessTokenWithWindowsIdentity(targetApplicationUri As Uri, identity As WindowsIdentity) As String
        Dim targetRealm As String = If(String.IsNullOrEmpty(Realm), GetRealmFromTargetUrl(targetApplicationUri), Realm)

        Dim claims As Claim() = If(identity IsNot Nothing, GetClaimsWithWindowsIdentity(identity), Nothing)

        Return GetS2SAccessTokenWithClaims(targetApplicationUri.Authority, targetRealm, claims)
    End Function

    ''' <summary>
    '''使用由应用程序的专有证书签名的访问令牌检索 S2S 客户端上下文
    '''代表指定的 WindowsIdentity 并拟用于 targetApplicationUri 处的应用程序
    ''' targetRealm。如果 web.config 中未指定领域，则会将身份验证质询发给
    ''' 以发现它。
    ''' </summary>
    ''' <param name="targetApplicationUri">目标 SharePoint 网站的 URL</param>
    ''' <param name="identity">代表用户创建访问令牌的 Windows 标识</param>
    ''' <returns>使用带有目标应用程序访问群体的访问令牌的 ClientContext</returns>
    Public Shared Function GetS2SClientContextWithWindowsIdentity(targetApplicationUri As Uri, identity As WindowsIdentity) As ClientContext
        Dim targetRealm As String = If(String.IsNullOrEmpty(Realm), GetRealmFromTargetUrl(targetApplicationUri), Realm)

        Dim claims As Claim() = If(identity IsNot Nothing, GetClaimsWithWindowsIdentity(identity), Nothing)

        Dim accessToken As String = GetS2SAccessTokenWithClaims(targetApplicationUri.Authority, targetRealm, claims)

        Return GetClientContextWithAccessToken(targetApplicationUri.ToString(), accessToken)
    End Function

    ''' <summary>
    ''' 从 SharePoint 获取身份验证领域
    ''' </summary>
    ''' <param name="targetApplicationUri">目标 SharePoint 网站的 URL</param>
    ''' <returns>领域 GUID 的字符串表示形式</returns>
    Public Shared Function GetRealmFromTargetUrl(targetApplicationUri As Uri) As String
        Dim request As WebRequest = HttpWebRequest.Create(targetApplicationUri.ToString() & "/_vti_bin/client.svc")
        request.Headers.Add("Authorization: Bearer ")

        Try
            request.GetResponse().Close()
        Catch e As WebException
            If e.Response Is Nothing Then
                Return Nothing
            End If

            Dim bearerResponseHeader As String = e.Response.Headers("WWW-Authenticate")
            If String.IsNullOrEmpty(bearerResponseHeader) Then
                Return Nothing
            End If

            Const bearer As String = "Bearer realm="""
            Dim bearerIndex As Integer = bearerResponseHeader.IndexOf(bearer, StringComparison.Ordinal)
            If bearerIndex < 0 Then
                Return Nothing
            End If

            Dim realmIndex As Integer = bearerIndex + bearer.Length

            If bearerResponseHeader.Length >= realmIndex + 36 Then
                Dim targetRealm As String = bearerResponseHeader.Substring(realmIndex, 36)

                Dim realmGuid As Guid

                If Guid.TryParse(targetRealm, realmGuid) Then
                    Return targetRealm
                End If
            End If
        End Try
        Return Nothing
    End Function

    ''' <summary>
    ''' 确定这是否是一个高信任外接程序。
    ''' </summary>
    ''' <returns>若是一个高信任外接程序，则为 True。</returns>
    Public Shared Function IsHighTrustApp() As Boolean
        Return SigningCredentials IsNot Nothing
    End Function

    ''' <summary>
    ''' 确保指定的 URL 在不为 Null 或空时以“/”结束。
    ''' </summary>
    ''' <param name="url">URL。</param>
    ''' <returns>如果 URL 不为 Null 或空，则该 URL 将以“/”结束。</returns>
    Public Shared Function EnsureTrailingSlash(url As String) As String
        If Not String.IsNullOrEmpty(url) AndAlso url(url.Length - 1) <> "/"c Then
            Return url + "/"
        End If

        Return url
    End Function

    ''' <summary>
    '''返回当前的时期时间(秒)
    ''' </summary>
    ''' <returns>以秒表示的时期时间</returns>
    Public Shared Function EpochTimeNow() As Long
        Return (DateTime.UtcNow - New DateTime(1970, 1, 1).ToUniversalTime()).TotalSeconds
    End Function

#End Region

#Region "私有字段"

    '
    ' 配置常数
    '

    Private Const AuthorizationPage As String = "_layouts/15/OAuthAuthorize.aspx"
    Private Const RedirectPage As String = "_layouts/15/AppRedirect.aspx"
    Private Const AcsPrincipalName As String = "00000001-0000-0000-c000-000000000000"
    Private Const AcsMetadataEndPointRelativeUrl As String = "metadata/json/1"
    Private Const S2SProtocol As String = "OAuth2"
    Private Const DelegationIssuance As String = "DelegationIssuance1.0"
    Private Const NameIdentifierClaimType As String = "nameid"
    Private Const TrustedForImpersonationClaimType As String = "trustedfordelegation"
    Private Const ActorTokenClaimType As String = "actortoken"

    '
    ' 环境常数
    '

    Private Shared GlobalEndPointPrefix As String = "accounts"
    Private Shared AcsHostUrl As String = "accesscontrol.windows.net"

    '
    ' 托管外接程序配置
    '
    Private Shared ReadOnly ClientId As String = If(String.IsNullOrEmpty(WebConfigurationManager.AppSettings.[Get]("ClientId")), WebConfigurationManager.AppSettings.[Get]("HostedAppName"), WebConfigurationManager.AppSettings.[Get]("ClientId"))

    Private Shared ReadOnly IssuerId As String = If(String.IsNullOrEmpty(WebConfigurationManager.AppSettings.[Get]("IssuerId")), ClientId, WebConfigurationManager.AppSettings.[Get]("IssuerId"))

    Private Shared ReadOnly HostedAppHostName As String = WebConfigurationManager.AppSettings.[Get]("HostedAppHostName")

    Private Shared ReadOnly HostedAppHostNameOverride As String = WebConfigurationManager.AppSettings.[Get]("HostedAppHostNameOverride")

    Private Shared ReadOnly ClientSecret As String = If(String.IsNullOrEmpty(WebConfigurationManager.AppSettings.[Get]("ClientSecret")), WebConfigurationManager.AppSettings.[Get]("HostedAppSigningKey"), WebConfigurationManager.AppSettings.[Get]("ClientSecret"))

    Private Shared ReadOnly SecondaryClientSecret As String = WebConfigurationManager.AppSettings.[Get]("SecondaryClientSecret")

    Private Shared ReadOnly Realm As String = WebConfigurationManager.AppSettings.[Get]("Realm")

    Private Shared ReadOnly ServiceNamespace As String = WebConfigurationManager.AppSettings.[Get]("Realm")

    Private Shared ReadOnly ClientSigningCertificatePath As String = WebConfigurationManager.AppSettings.[Get]("ClientSigningCertificatePath")

    Private Shared ReadOnly ClientSigningCertificatePassword As String = WebConfigurationManager.AppSettings.[Get]("ClientSigningCertificatePassword")

    Private Shared ReadOnly ClientCertificate As X509Certificate2 = If((String.IsNullOrEmpty(ClientSigningCertificatePath) OrElse String.IsNullOrEmpty(ClientSigningCertificatePassword)), Nothing, New X509Certificate2(ClientSigningCertificatePath, ClientSigningCertificatePassword))

    Private Shared ReadOnly SigningCredentials As X509SigningCredentials = If(ClientCertificate Is Nothing, Nothing, New X509SigningCredentials(ClientCertificate, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha256))

#End Region

#Region "私有方法"

    Private Shared Function CreateAcsClientContextForUrl(properties As SPRemoteEventProperties, sharepointUrl As Uri) As ClientContext
        Dim contextTokenString As String = properties.ContextToken

        If [String].IsNullOrEmpty(contextTokenString) Then
            Return Nothing
        End If

        Dim contextToken As SharePointContextToken = ReadAndValidateContextToken(contextTokenString, OperationContext.Current.IncomingMessageHeaders.To.Host)

        Dim accessToken As String = GetAccessToken(contextToken, sharepointUrl.Authority).AccessToken
        Return GetClientContextWithAccessToken(sharepointUrl.ToString(), accessToken)
    End Function

    Private Shared Function GetAcsMetadataEndpointUrl() As String
        Return Path.Combine(GetAcsGlobalEndpointUrl(), AcsMetadataEndPointRelativeUrl)
    End Function

    Private Shared Function GetFormattedPrincipal(principalName As String, hostName As String, targetRealm As String) As String
        If Not [String].IsNullOrEmpty(hostName) Then
            Return [String].Format(CultureInfo.InvariantCulture, "{0}/{1}@{2}", principalName, hostName, targetRealm)
        End If

        Return [String].Format(CultureInfo.InvariantCulture, "{0}@{1}", principalName, targetRealm)
    End Function

    Private Shared Function GetAcsPrincipalName(targetRealm As String) As String
        Return GetFormattedPrincipal(AcsPrincipalName, New Uri(GetAcsGlobalEndpointUrl()).Host, targetRealm)
    End Function

    Private Shared Function GetAcsGlobalEndpointUrl() As String
        Return [String].Format(CultureInfo.InvariantCulture, "https://{0}.{1}/", GlobalEndPointPrefix, AcsHostUrl)
    End Function

    Private Shared Function CreateJwtSecurityTokenHandler() As JwtSecurityTokenHandler
        Return New JwtSecurityTokenHandler()
    End Function

    Private Shared Function GetS2SAccessTokenWithClaims(targetApplicationHostName As String, targetRealm As String, claims As IEnumerable(Of Claim)) As String
        Return IssueToken(ClientId, IssuerId, targetRealm, SharePointPrincipal, targetRealm, targetApplicationHostName, True,
                          claims, claims Is Nothing)
    End Function

    Private Shared Function GetClaimsWithWindowsIdentity(identity As WindowsIdentity) As Claim()
        Dim claims As Claim() = New Claim() _
                {New Claim(NameIdentifierClaimType, identity.User.Value.ToLower()),
                 New Claim("nii", "urn:office:idp:activedirectory")}
        Return claims
    End Function

    Private Shared Function IssueToken(sourceApplication As String, issuerApplication As String, sourceRealm As String, targetApplication As String, targetRealm As String, targetApplicationHostName As String, trustedForDelegation As Boolean,
                                       claims As IEnumerable(Of Claim), Optional appOnly As Boolean = False) As String
        If SigningCredentials Is Nothing Then
            Throw New InvalidOperationException("SigningCredentials was not initialized")
        End If

        '#Region "Actor token"

        Dim issuer As String = If(String.IsNullOrEmpty(sourceRealm), issuerApplication, String.Format("{0}@{1}", issuerApplication, sourceRealm))
        Dim nameid As String = If(String.IsNullOrEmpty(sourceRealm), sourceApplication, String.Format("{0}@{1}", sourceApplication, sourceRealm))
        Dim audience As String = String.Format("{0}/{1}@{2}", targetApplication, targetApplicationHostName, targetRealm)

        Dim actorClaims As New List(Of Claim)()
        actorClaims.Add(New Claim(NameIdentifierClaimType, nameid))
        If trustedForDelegation AndAlso Not appOnly Then
            actorClaims.Add(New Claim(TrustedForImpersonationClaimType, "true"))
        End If

        ' 创建标记
        Dim actorToken As New JwtSecurityToken(issuer:=issuer, audience:=audience, claims:=actorClaims, notBefore:=DateTime.UtcNow, expires:=DateTime.UtcNow.Add(HighTrustAccessTokenLifetime), signingCredentials:=SigningCredentials)

        Dim actorTokenString As String = New JwtSecurityTokenHandler().WriteToken(actorToken)

        If appOnly Then
            ' 在委托情况下，只允许应用程序使用的标记与参与者标记相同
            Return actorTokenString
        End If

        '#End Region

        '#Region "Outer token"

        Dim outerClaims As List(Of Claim) = If(claims Is Nothing, New List(Of Claim)(), New List(Of Claim)(claims))
        outerClaims.Add(New Claim(ActorTokenClaimType, actorTokenString))

        ' 外部标记颁发者应与参与者标记的 nameid 匹配
        Dim jsonToken As New JwtSecurityToken(nameid, audience, outerClaims, DateTime.UtcNow, DateTime.UtcNow.Add(HighTrustAccessTokenLifetime))

        Dim accessToken As String = New JwtSecurityTokenHandler().WriteToken(jsonToken)

        '#End Region

        Return accessToken
    End Function

#End Region

#Region "AcsMetadataParser"

    ' 该类用于从全局 STS 终结点获取元数据文档。 它包含
    ' 分析元数据文档以及获取终结点和 STS 证书的方法。
    Public NotInheritable Class AcsMetadataParser
        Private Sub New()
        End Sub

        Public Shared Function GetAcsSigningCert(realm As String) As X509Certificate2
            Dim document As JsonMetadataDocument = GetMetadataDocument(realm)

            If document.keys IsNot Nothing AndAlso document.keys.Count > 0 Then
                Dim signingKey As JsonKey = document.keys(0)

                If signingKey IsNot Nothing AndAlso signingKey.keyValue IsNot Nothing Then
                    Return New X509Certificate2(Encoding.UTF8.GetBytes(signingKey.keyValue.value))
                End If
            End If

            Throw New Exception("Metadata document does not contain ACS signing certificate.")
        End Function

        Public Shared Function GetDelegationServiceUrl(realm As String) As String
            Dim document As JsonMetadataDocument = GetMetadataDocument(realm)

            Dim delegationEndpoint As JsonEndpoint = document.endpoints.SingleOrDefault(Function(e) e.protocol = DelegationIssuance)

            If delegationEndpoint IsNot Nothing Then
                Return delegationEndpoint.location
            End If

            Throw New Exception("Metadata document does not contain Delegation Service endpoint Url")
        End Function

        Private Shared Function GetMetadataDocument(realm As String) As JsonMetadataDocument
            Dim acsMetadataEndpointUrlWithRealm As String = [String].Format(CultureInfo.InvariantCulture, "{0}?realm={1}", GetAcsMetadataEndpointUrl(), realm)
            Dim acsMetadata As Byte()
            Using webClient As New WebClient()
                acsMetadata = webClient.DownloadData(acsMetadataEndpointUrlWithRealm)
            End Using
            Dim jsonResponseString As String = Encoding.UTF8.GetString(acsMetadata)

            Dim serializer As New JavaScriptSerializer()
            Dim document As JsonMetadataDocument = serializer.Deserialize(Of JsonMetadataDocument)(jsonResponseString)

            If document Is Nothing Then
                Throw New Exception("No metadata document found at the global endpoint " & acsMetadataEndpointUrlWithRealm)
            End If

            Return document
        End Function

        Public Shared Function GetStsUrl(realm As String) As String
            Dim document As JsonMetadataDocument = GetMetadataDocument(realm)

            Dim s2sEndpoint As JsonEndpoint = document.endpoints.SingleOrDefault(Function(e) e.protocol = S2SProtocol)

            If s2sEndpoint IsNot Nothing Then
                Return s2sEndpoint.location
            End If

            Throw New Exception("Metadata document does not contain STS endpoint url")
        End Function

        Private Class JsonMetadataDocument
            Public Property serviceName() As String
                Get
                    Return m_serviceName
                End Get
                Set(value As String)
                    m_serviceName = value
                End Set
            End Property

            Private m_serviceName As String

            Public Property endpoints() As List(Of JsonEndpoint)
                Get
                    Return m_endpoints
                End Get
                Set(value As List(Of JsonEndpoint))
                    m_endpoints = value
                End Set
            End Property

            Private m_endpoints As List(Of JsonEndpoint)

            Public Property keys() As List(Of JsonKey)
                Get
                    Return m_keys
                End Get
                Set(value As List(Of JsonKey))
                    m_keys = value
                End Set
            End Property

            Private m_keys As List(Of JsonKey)
        End Class

        Private Class JsonEndpoint
            Public Property location() As String
                Get
                    Return m_location
                End Get
                Set(value As String)
                    m_location = value
                End Set
            End Property

            Private m_location As String

            Public Property protocol() As String
                Get
                    Return m_protocol
                End Get
                Set(value As String)
                    m_protocol = value
                End Set
            End Property

            Private m_protocol As String

            Public Property usage() As String
                Get
                    Return m_usage
                End Get
                Set(value As String)
                    m_usage = value
                End Set
            End Property

            Private m_usage As String
        End Class

        Private Class JsonKeyValue
            Public Property type() As String
                Get
                    Return m_type
                End Get
                Set(value As String)
                    m_type = value
                End Set
            End Property

            Private m_type As String

            Public Property value() As String
                Get
                    Return m_value
                End Get
                Set(value As String)
                    m_value = value
                End Set
            End Property

            Private m_value As String
        End Class

        Private Class JsonKey
            Public Property usage() As String
                Get
                    Return m_usage
                End Get
                Set(value As String)
                    m_usage = value
                End Set
            End Property

            Private m_usage As String

            Public Property keyValue() As JsonKeyValue
                Get
                    Return m_keyValue
                End Get
                Set(value As JsonKeyValue)
                    m_keyValue = value
                End Set
            End Property

            Private m_keyValue As JsonKeyValue
        End Class
    End Class

#End Region
End Class

''' <summary>
''' 由 SharePoint 生成的 JwtSecurityToken，它可对第三方应用程序进行身份验证，并允许使用刷新标记进行回拨
''' </summary>
Public Class SharePointContextToken
    Inherits JwtSecurityToken

    Public Shared Function Create(contextToken As JwtSecurityToken) As SharePointContextToken
        Return New SharePointContextToken(contextToken.Issuer, contextToken.Audiences.FirstOrDefault, contextToken.ValidFrom, contextToken.ValidTo, contextToken.Claims)
    End Function

    Public Sub New(issuer As String, audience As String, validFrom As DateTime, validTo As DateTime, claims As IEnumerable(Of Claim))
        MyBase.New(issuer, audience, claims, validFrom, validTo)
    End Sub

    Public Sub New(issuer As String, audience As String, validFrom As DateTime, validTo As DateTime, claims As IEnumerable(Of Claim), issuerToken As SecurityToken, actorToken As JwtSecurityToken)
        '为了向后兼容与以前版本的 TokenHelper 提供此方法。
        '当前版本的 JwtSecurityToken 没有采用以上的所有参数的构造函数。

        MyBase.New(issuer, audience, claims, validFrom, validTo, actorToken.SigningCredentials)
    End Sub

    Public Sub New(issuer As String, audience As String, validFrom As DateTime, validTo As DateTime, claims As IEnumerable(Of Claim), signingCredentials As SigningCredentials)
        MyBase.New(issuer, audience, claims, validFrom, validTo, signingCredentials)
    End Sub

    Public ReadOnly Property NameId() As String
        Get
            Return GetClaimValue(Me, "nameid")
        End Get
    End Property

    ''' <summary>
    ''' 上下文标记 "appctxsender" 声明的主体名称部分
    ''' </summary>
    Public ReadOnly Property TargetPrincipalName() As String
        Get
            Dim appctxsender As String = GetClaimValue(Me, "appctxsender")

            If appctxsender Is Nothing Then
                Return Nothing
            End If

            Return appctxsender.Split("@"c)(0)
        End Get
    End Property

    ''' <summary>
    ''' 上下文标记的 "refreshtoke" 声明
    ''' </summary>
    Public ReadOnly Property RefreshToken() As String
        Get
            Return GetClaimValue(Me, "refreshtoken")
        End Get
    End Property

    ''' <summary>
    ''' 上下文标记的 "CacheKey" 声明
    ''' </summary>
    Public ReadOnly Property CacheKey() As String
        Get
            Dim appctx As String = GetClaimValue(Me, "appctx")
            If appctx Is Nothing Then
                Return Nothing
            End If

            Dim ctx As New ClientContext("http://tempuri.org")
            Dim dict As Dictionary(Of String, Object) = DirectCast(ctx.ParseObjectFromJsonString(appctx), Dictionary(Of String, Object))
            Dim cacheKeyString As String = DirectCast(dict("CacheKey"), String)

            Return cacheKeyString
        End Get
    End Property

    ''' <summary>
    ''' 上下文标记的 "SecurityTokenServiceUri" 声明
    ''' </summary>
    Public ReadOnly Property SecurityTokenServiceUri() As String
        Get
            Dim appctx As String = GetClaimValue(Me, "appctx")
            If appctx Is Nothing Then
                Return Nothing
            End If

            Dim ctx As New ClientContext("http://tempuri.org")
            Dim dict As Dictionary(Of String, Object) = DirectCast(ctx.ParseObjectFromJsonString(appctx), Dictionary(Of String, Object))
            Dim securityTokenServiceUriString As String = DirectCast(dict("SecurityTokenServiceUri"), String)

            Return securityTokenServiceUriString
        End Get
    End Property

    ''' <summary>
    ''' 上下文标记 "audience" 声明的领域部分
    ''' </summary>
    Public ReadOnly Property Realm() As String
        Get
            For Each aud As String In Audiences
                Dim tokenRealm As String = aud.Substring(aud.IndexOf("@"c) + 1)
                If String.IsNullOrEmpty(tokenRealm) Then
                    Continue For
                Else
                    Return tokenRealm
                End If
            Next
            Return Nothing
        End Get
    End Property

    Private Shared Function GetClaimValue(token As JwtSecurityToken, claimType As String) As String
        If token Is Nothing Then
            Throw New ArgumentNullException("token")
        End If

        For Each claim As Claim In token.Claims
            If StringComparer.Ordinal.Equals(claim.Type, claimType) Then
                Return claim.Value
            End If
        Next

        Return Nothing
    End Function
End Class

''' <summary>
''' 表示含有多个使用对称算法生成的安全密钥的安全标记。
''' </summary>
Public Class MultipleSymmetricKeySecurityToken
    Inherits SecurityToken

    ''' <summary>
    ''' 对 MultipleSymmetricKeySecurityToken 类的新实例进行初始化。
    ''' </summary>
    ''' <param name="keys">包含对称密钥的字节数组枚举。</param>
    Public Sub New(keys As IEnumerable(Of Byte()))
        Me.New(Microsoft.IdentityModel.Tokens.UniqueId.CreateUniqueId(), keys)
    End Sub

    ''' <summary>
    ''' 对 MultipleSymmetricKeySecurityToken 类的新实例进行初始化。
    ''' </summary>
    ''' <param name="tokenId">安全标记的唯一标识符。</param>
    ''' <param name="keys">包含对称密钥的字节数组枚举。</param>
    Public Sub New(tokenId As String, keys As IEnumerable(Of Byte()))
        If keys Is Nothing Then
            Throw New ArgumentNullException("keys")
        End If

        If String.IsNullOrEmpty(tokenId) Then
            Throw New ArgumentException("Value cannot be a null or empty string.", "tokenId")
        End If

        For Each key As Byte() In keys
            If key.Length <= 0 Then
                Throw New ArgumentException("The key length must be greater then zero.", "keys")
            End If
        Next

        m_id = tokenId
        m_effectiveTime = DateTime.UtcNow
        m_securityKeys = CreateSymmetricSecurityKeys(keys)
    End Sub

    ''' <summary>
    ''' 获取安全标记的唯一标识符。
    ''' </summary>
    Public Overrides ReadOnly Property Id As String
        Get
            Return m_id
        End Get
    End Property

    ''' <summary>
    ''' 获取与安全标记关联的加密密钥。
    ''' </summary>
    Public Overrides ReadOnly Property SecurityKeys() As ReadOnlyCollection(Of SecurityKey)
        Get
            Return m_securityKeys.AsReadOnly()
        End Get
    End Property

    ''' <summary>
    ''' 在安全标记有效后及时获取第一个瞬间。
    ''' </summary>
    Public Overrides ReadOnly Property ValidFrom As DateTime
        Get
            Return m_effectiveTime
        End Get
    End Property

    ''' <summary>
    ''' 在安全标记有效后及时获取最后一个瞬间。
    ''' </summary>
    Public Overrides ReadOnly Property ValidTo As DateTime
        Get
            ' 永不过期
            Return Date.MaxValue
        End Get
    End Property

    ''' <summary>
    ''' 返回一个值，用于指示此实例的密钥标识符是否可以分析到指定密钥标识符。
    ''' </summary>
    ''' <param name="keyIdentifierClause">要与此实例进行比较的 SecurityKeyIdentifierClause。</param>
    ''' <returns>如果 keyIdentifierClause 为 SecurityKeyIdentifierClause，并且具有与 ID 属性相同的唯一标识符，则为 true；否则为 false。</returns>
    Public Overrides Function MatchesKeyIdentifierClause(keyIdentifierClause As SecurityKeyIdentifierClause) As Boolean
        If keyIdentifierClause Is Nothing Then
            Throw New ArgumentNullException("keyIdentifierClause")
        End If

        Return MyBase.MatchesKeyIdentifierClause(keyIdentifierClause)
    End Function

#Region "私有成员"

    Private Function CreateSymmetricSecurityKeys(keys As IEnumerable(Of Byte())) As List(Of SecurityKey)
        Dim symmetricKeys As New List(Of SecurityKey)()
        For Each key As Byte() In keys
            symmetricKeys.Add(New InMemorySymmetricSecurityKey(key))
        Next
        Return symmetricKeys
    End Function

    Private m_id As String
    Private m_effectiveTime As DateTime
    Private m_securityKeys As List(Of SecurityKey)

#End Region
End Class

''' <summary>
'''表示 ACS 服务器调用中的 OAuth 响应。
''' </summary>
Public Class OAuthTokenResponse

    ''' <summary>
    '''默认构造函数。
    ''' </summary>
    Public Sub New()
    End Sub

    ''' <summary>
    '''在从 ACS 服务器返回的字节数组中构造 OAuthTokenResponse 对象。
    ''' </summary>
    ''' <param name="response">从 ACS 获得的原始字节数组。</param>
    Public Sub New(ByVal response As Byte())
        Dim serializer = New JavaScriptSerializer()
        Me.Data = TryCast(serializer.DeserializeObject(Encoding.UTF8.GetString(response)), Dictionary(Of String, Object))
        Me.AccessToken = Me.GetValue("access_token")
        Me.TokenType = Me.GetValue("token_type")
        Me.Resource = Me.GetValue("resource")
        Me.UserType = Me.GetValue("user_type")
        Dim epochTime As Long = 0

        If Long.TryParse(Me.GetValue("expires_in"), epochTime) Then
            Me.ExpiresIn = epochTime
        End If

        If Long.TryParse(Me.GetValue("expires_on"), epochTime) Then
            Me.ExpiresOn = epochTime
        End If

        If Long.TryParse(Me.GetValue("not_before"), epochTime) Then
            Me.NotBefore = epochTime
        End If

        If Long.TryParse(Me.GetValue("extended_expires_in"), epochTime) Then
            Me.ExtendedExpiresIn = epochTime
        End If
    End Sub

    ''' <summary>
    '''获取访问令牌。
    ''' </summary>
    Public Property AccessToken As String

    ''' <summary>
    '''从原始响应获取分析的数据。
    ''' </summary>
    Public ReadOnly Property Data As IDictionary(Of String, Object)

    ''' <summary>
    '''获取以 Epoch 时间表示的到期时间。
    ''' </summary>
    Public ReadOnly Property ExpiresIn As Long

    ''' <summary>
    '''获取时期时间中的过期时间。
    ''' </summary>
    Public ReadOnly Property ExpiresOn As Long

    ''' <summary>
    '''获取 extended expires in 时期时间。
    ''' </summary>
    Public ReadOnly Property ExtendedExpiresIn As Long

    ''' <summary>
    '''获取时期时间之前的过期时间。
    ''' </summary>
    Public ReadOnly Property NotBefore As Long

    ''' <summary>
    '''获取资源。
    ''' </summary>
    Public ReadOnly Property Resource As String

    ''' <summary>
    '''获取标记类型。
    ''' </summary>
    Public ReadOnly Property TokenType As String

    ''' <summary>
    '''获取用户类型。
    ''' </summary>
    Public ReadOnly Property UserType As String

    ''' <summary>
    '''通过键从数据中获取值。
    ''' </summary>
    ''' <param name="key">键。</param>
    ''' <returns>如果键值存在，则为键值，否则为空字符串。</returns>
    Private Function GetValue(ByVal key As String) As String
        Dim value As Object = Nothing

        If Me.Data.TryGetValue(key, value) Then
            Return TryCast(value, String)
        Else
            Return String.Empty
        End If
    End Function
End Class

''' <summary>
'''表示 Web 客户端，用于向 ACS 服务器发出 OAuth 调用。
''' </summary>
Public Class OAuthClient

    ''' <summary>
    '''使用刷新标记获得 OAuthTokenResponse。
    ''' </summary>
    ''' <param name="uri">ACS 服务器的 URI。</param>
    ''' <param name="clientId">客户端 ID。</param>
    ''' <param name="ClientSecret">客户端密钥。</param>
    ''' <param name="refreshToken">刷新标记。</param>
    ''' <param name="resource">资源。</param>
    ''' <returns>从 ACS 服务器响应。</returns>
    Public Shared Function GetAccessTokenWithRefreshToken(ByVal uri As String, ByVal clientId As String, ByVal ClientSecret As String, ByVal refreshToken As String, ByVal resource As String) As OAuthTokenResponse
        Dim client As WebClient = New WebClient()
        Dim values As NameValueCollection = New NameValueCollection From {
            {"grant_type", "refresh_token"},
            {"client_id", clientId},
            {"client_secret", ClientSecret},
            {"refresh_token", refreshToken},
            {"resource", resource}
        }
        Dim response As Byte() = client.UploadValues(uri, "POST", values)
        Return New OAuthTokenResponse(response)
    End Function

    ''' <summary>
    '''使用客户端凭据获得 OAuthTokenResponse。
    ''' </summary>
    ''' <param name="uri">ACS 服务器的 URI。</param>
    ''' <param name="clientId">客户端 ID。</param>
    ''' <param name="ClientSecret">客户端密钥。</param>
    ''' <param name="resource">资源。</param>
    ''' <returns>从 ACS 服务器响应。</returns>
    Public Shared Function GetAccessTokenWithClientCredentials(ByVal uri As String, ByVal clientId As String, ByVal ClientSecret As String, ByVal resource As String) As OAuthTokenResponse
        Dim client As WebClient = New WebClient()
        Dim values As NameValueCollection = New NameValueCollection From {
            {"grant_type", "client_credentials"},
            {"client_id", clientId},
            {"client_secret", ClientSecret},
            {"resource", resource}
        }
        Dim response As Byte() = client.UploadValues(uri, "POST", values)
        Return New OAuthTokenResponse(response)
    End Function

    ''' <summary>
    '''使用授权代码获得 OAuthTokenResponse。
    ''' </summary>
    ''' <param name="uri">ACS 服务器的 URI。</param>
    ''' <param name="clientId">客户端 ID。</param>
    ''' <param name="ClientSecret">客户端密钥。</param>
    ''' <param name="authorizationCode">授权代码。</param>
    ''' <param name="redirectUri">重定向 Uri。</param>
    ''' <param name="resource">资源。</param>
    ''' <returns>从 ACS 服务器响应。</returns>
    Public Shared Function GetAccessTokenWithAuthorizationCode(ByVal uri As String, ByVal clientId As String, ByVal ClientSecret As String, ByVal authorizationCode As String, ByVal redirectUri As String, ByVal resource As String) As OAuthTokenResponse
        Dim client As WebClient = New WebClient()
        Dim values As NameValueCollection = New NameValueCollection From {
            {"grant_type", "authorization_code"},
            {"client_id", clientId},
            {"client_secret", ClientSecret},
            {"code", authorizationCode},
            {"redirect_uri", redirectUri},
            {"resource", resource}
        }
        Dim response As Byte() = client.UploadValues(uri, "POST", values)
        Return New OAuthTokenResponse(response)
    End Function
End Class
