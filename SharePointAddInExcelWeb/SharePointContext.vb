Imports System.IdentityModel.Tokens
Imports System.Net
Imports System.Security.Principal
Imports Microsoft.SharePoint.Client

''' <summary>
''' 封装 SharePoint 中的所有信息。
''' </summary>
Public MustInherit Class SharePointContext
    Public Const SPHostUrlKey As String = "SPHostUrl"
    Public Const SPAppWebUrlKey As String = "SPAppWebUrl"
    Public Const SPLanguageKey As String = "SPLanguage"
    Public Const SPClientTagKey As String = "SPClientTag"
    Public Const SPProductNumberKey As String = "SPProductNumber"

    Protected Shared ReadOnly AccessTokenLifetimeTolerance As Long = 5 * 60 '5 分钟

    Private ReadOnly m_spHostUrl As Uri
    Private ReadOnly m_spAppWebUrl As Uri
    Private ReadOnly m_spLanguage As String
    Private ReadOnly m_spClientTag As String
    Private ReadOnly m_spProductNumber As String

    ' <AccessTokenString，以 Epoch 时间表示的 UtcExpiresOn>
    Protected m_userAccessTokenForSPHost As Tuple(Of String, Long)
    Protected m_userAccessTokenForSPAppWeb As Tuple(Of String, Long)
    Protected m_appOnlyAccessTokenForSPHost As Tuple(Of String, Long)
    Protected m_appOnlyAccessTokenForSPAppWeb As Tuple(Of String, Long)

    ''' <summary>
    ''' 从指定的 HTTP 请求的 QueryString 中获取 SharePoint 宿主 URL。
    ''' </summary>
    ''' <param name="httpRequest">指定的 HTTP 请求。</param>
    ''' <returns>SharePoint 宿主 URL。如果 HTTP 请求不包含 SharePoint 宿主 URL，则返回 <c>Nothing</c>。</returns>
    Public Shared Function GetSPHostUrl(httpRequest As HttpRequestBase) As Uri
        If httpRequest Is Nothing Then
            Throw New ArgumentNullException("httpRequest")
        End If

        Dim spHostUrlString As String = TokenHelper.EnsureTrailingSlash(httpRequest.QueryString(SPHostUrlKey))
        Dim spHostUrl As Uri = Nothing
        If Uri.TryCreate(spHostUrlString, UriKind.Absolute, spHostUrl) AndAlso
           (spHostUrl.Scheme = Uri.UriSchemeHttp OrElse spHostUrl.Scheme = Uri.UriSchemeHttps) Then
            Return spHostUrl
        End If

        Return Nothing
    End Function

    ''' <summary>
    ''' 从指定的 HTTP 请求的 QueryString 中获取 SharePoint 宿主 URL。
    ''' </summary>
    ''' <param name="httpRequest">指定的 HTTP 请求。</param>
    ''' <returns>SharePoint 宿主 URL。如果 HTTP 请求不包含 SharePoint 宿主 URL，则返回 <c>Nothing</c>。</returns>
    Public Shared Function GetSPHostUrl(httpRequest As HttpRequest) As Uri
        Return GetSPHostUrl(New HttpRequestWrapper(httpRequest))
    End Function

    ''' <summary>
    ''' SharePoint 宿主 URL。
    ''' </summary>
    Public ReadOnly Property SPHostUrl() As Uri
        Get
            Return Me.m_spHostUrl
        End Get
    End Property

    ''' <summary>
    ''' SharePoint 应用程序网站 URL。
    ''' </summary>
    Public ReadOnly Property SPAppWebUrl() As Uri
        Get
            Return Me.m_spAppWebUrl
        End Get
    End Property

    ''' <summary>
    ''' SharePoint 语言。
    ''' </summary>
    Public ReadOnly Property SPLanguage() As String
        Get
            Return Me.m_spLanguage
        End Get
    End Property

    ''' <summary>
    ''' SharePoint 客户端标记。
    ''' </summary>
    Public ReadOnly Property SPClientTag() As String
        Get
            Return Me.m_spClientTag
        End Get
    End Property

    ''' <summary>
    ''' SharePoint 产品编号。
    ''' </summary>
    Public ReadOnly Property SPProductNumber() As String
        Get
            Return Me.m_spProductNumber
        End Get
    End Property

    ''' <summary>
    ''' 适用于 SharePoint 宿主的用户访问令牌。
    ''' </summary>
    Public MustOverride ReadOnly Property UserAccessTokenForSPHost() As String

    ''' <summary>
    ''' 适用于 SharePoint 应用程序网站的用户访问令牌。
    ''' </summary>
    Public MustOverride ReadOnly Property UserAccessTokenForSPAppWeb() As String

    ''' <summary>
    ''' 适用于 SharePoint Web 宿主的应用程序专用访问令牌。
    ''' </summary>
    Public MustOverride ReadOnly Property AppOnlyAccessTokenForSPHost() As String

    ''' <summary>
    ''' 适用于 SharePoint 应用程序网站的应用程序专用访问令牌。
    ''' </summary>
    Public MustOverride ReadOnly Property AppOnlyAccessTokenForSPAppWeb() As String

    ''' <summary>
    ''' 构造函数。
    ''' </summary>
    ''' <param name="spHostUrl">SharePoint 宿主 URL。</param>
    ''' <param name="spAppWebUrl">SharePoint 应用程序网站 URL。</param>
    ''' <param name="spLanguage">SharePoint 语言。</param>
    ''' <param name="spClientTag">SharePoint 客户端标记。</param>
    ''' <param name="spProductNumber">SharePoint 产品编号。</param>
    Protected Sub New(spHostUrl As Uri, spAppWebUrl As Uri, spLanguage As String, spClientTag As String, spProductNumber As String)
        If spHostUrl Is Nothing Then
            Throw New ArgumentNullException("spHostUrl")
        End If

        If String.IsNullOrEmpty(spLanguage) Then
            Throw New ArgumentNullException("spLanguage")
        End If

        If String.IsNullOrEmpty(spClientTag) Then
            Throw New ArgumentNullException("spClientTag")
        End If

        If String.IsNullOrEmpty(spProductNumber) Then
            Throw New ArgumentNullException("spProductNumber")
        End If

        Me.m_spHostUrl = spHostUrl
        Me.m_spAppWebUrl = spAppWebUrl
        Me.m_spLanguage = spLanguage
        Me.m_spClientTag = spClientTag
        Me.m_spProductNumber = spProductNumber
    End Sub

    ''' <summary>
    ''' 为 SharePoint 宿主创建一个用户 ClientContext。
    ''' </summary>
    ''' <returns>ClientContext 实例。</returns>
    Public Function CreateUserClientContextForSPHost() As ClientContext
        Return CreateClientContext(Me.SPHostUrl, Me.UserAccessTokenForSPHost)
    End Function

    ''' <summary>
    ''' 创建适用于 SharePoint 应用程序网站的用户 ClientContext。
    ''' </summary>
    ''' <returns>ClientContext 实例。</returns>
    Public Function CreateUserClientContextForSPAppWeb() As ClientContext
        Return CreateClientContext(Me.SPAppWebUrl, Me.UserAccessTokenForSPAppWeb)
    End Function

    ''' <summary>
    ''' 创建适用于 SharePoint 宿主的应用程序专用 ClientContext。
    ''' </summary>
    ''' <returns>ClientContext 实例。</returns>
    Public Function CreateAppOnlyClientContextForSPHost() As ClientContext
        Return CreateClientContext(Me.SPHostUrl, Me.AppOnlyAccessTokenForSPHost)
    End Function

    ''' <summary>
    ''' 创建适用于 SharePoint 应用程序网站的应用程序专用 ClientContext。
    ''' </summary>
    ''' <returns>ClientContext 实例。</returns>
    Public Function CreateAppOnlyClientContextForSPAppWeb() As ClientContext
        Return CreateClientContext(Me.SPAppWebUrl, Me.AppOnlyAccessTokenForSPAppWeb)
    End Function

    ''' <summary>
    ''' 从自动托管外接程序的 SharePoint 获取数据库连接字符串。
    '''由于不再提供自动托管选项，已弃用此方法。
    ''' </summary>
    <ObsoleteAttribute("This method is deprecated because the autohosted option is no longer available.", true)>
    Public Function GetDatabaseConnectionString() As String
        Throw New NotSupportedException("This method is deprecated because the autohosted option is no longer available.")
    End Function

    ''' <summary>
    ''' 确定指定的访问令牌是否有效。
    ''' 如果访问令牌为 Nothing 或者已过期，则将其视为无效。
    ''' </summary>
    ''' <param name="accessToken">要验证的访问令牌。</param>
    ''' <returns>如果访问令牌有效，则为 True。</returns>
    Protected Shared Function IsAccessTokenValid(accessToken As Tuple(Of String, Long)) As Boolean
        Return accessToken IsNot Nothing AndAlso
               Not String.IsNullOrEmpty(accessToken.Item1) AndAlso
               accessToken.Item2 > TokenHelper.EpochTimeNow()
    End Function

    ''' <summary>
    ''' 使用指定的 SharePoint 网站 URL 和访问令牌创建 ClientContext。
    ''' </summary>
    ''' <param name="spSiteUrl">站点 URL。</param>
    ''' <param name="accessToken">访问令牌。</param>
    ''' <returns>ClientContext 实例。</returns>
    Private Shared Function CreateClientContext(spSiteUrl As Uri, accessToken As String) As ClientContext
        If spSiteUrl IsNot Nothing AndAlso Not String.IsNullOrEmpty(accessToken) Then
            Return TokenHelper.GetClientContextWithAccessToken(spSiteUrl.AbsoluteUri, accessToken)
        End If

        Return Nothing
    End Function
End Class

''' <summary>
''' 重定向状态。
''' </summary>
Public Enum RedirectionStatus
    Ok
    ShouldRedirect
    CanNotRedirect
End Enum

''' <summary>
''' 提供 SharePointContext 实例。
''' </summary>
Public MustInherit Class SharePointContextProvider
    Private Shared s_current As SharePointContextProvider

    ''' <summary>
    ''' 当前的 SharePointContextProvider 实例。
    ''' </summary>
    Public Shared ReadOnly Property Current() As SharePointContextProvider
        Get
            Return SharePointContextProvider.s_current
        End Get
    End Property

    ''' <summary>
    ''' 初始化默认的 SharePointContextProvider 实例。
    ''' </summary>
    Shared Sub New()
        If Not TokenHelper.IsHighTrustApp() Then
            SharePointContextProvider.s_current = New SharePointAcsContextProvider()
        Else
            SharePointContextProvider.s_current = New SharePointHighTrustContextProvider()
        End If
    End Sub

    ''' <summary>
    ''' 将指定的 SharePointContextProvider 实例注册为当前项。
    ''' 它应由 Global.asax 中的 Application_Start() 调用。
    ''' </summary>
    ''' <param name="provider">将 SharePointContextProvider 设置为当前项。</param>
    Public Shared Sub Register(provider As SharePointContextProvider)
        If provider Is Nothing Then
            Throw New ArgumentNullException("provider")
        End If

        SharePointContextProvider.s_current = provider
    End Sub

    ''' <summary>
    ''' 检查是否必须重定向到 SharePoint 供用户进行身份验证。
    ''' </summary>
    ''' <param name="httpContext">HTTP 上下文。</param>
    ''' <param name="redirectUrl">如果状态为“ShouldRedirect”，则返回指向 SharePoint 的重定向 URL。如果状态为“Ok”或“CanNotRedirect”，则返回 <c>Null</c>。</param>
    ''' <returns>重定向状态。</returns>
    Public Shared Function CheckRedirectionStatus(httpContext As HttpContextBase, ByRef redirectUrl As Uri) As RedirectionStatus
        If httpContext Is Nothing Then
            Throw New ArgumentNullException("httpContext")
        End If

        redirectUrl = Nothing
        Dim contextTokenExpired As Boolean = False

        Try
            If SharePointContextProvider.Current.GetSharePointContext(httpContext) IsNot Nothing Then
                Return RedirectionStatus.Ok
            End If
        Catch ex As SecurityTokenExpiredException
            contextTokenExpired = True
        End Try

        Const SPHasRedirectedToSharePointKey As String = "SPHasRedirectedToSharePoint"

        If Not String.IsNullOrEmpty(httpContext.Request.QueryString(SPHasRedirectedToSharePointKey)) AndAlso Not contextTokenExpired Then
            Return RedirectionStatus.CanNotRedirect
        End If

        Dim spHostUrl As Uri = SharePointContext.GetSPHostUrl(httpContext.Request)

        If spHostUrl Is Nothing Then
            Return RedirectionStatus.CanNotRedirect
        End If

        If StringComparer.OrdinalIgnoreCase.Equals(httpContext.Request.HttpMethod, "POST") Then
            Return RedirectionStatus.CanNotRedirect
        End If

        Dim requestUrl As Uri = httpContext.Request.Url

        Dim queryNameValueCollection = HttpUtility.ParseQueryString(requestUrl.Query)

        ' 移除 {StandardTokens} 中包括的值，因为 {StandardTokens} 将插入在查询字符串的开头。
        queryNameValueCollection.Remove(SharePointContext.SPHostUrlKey)
        queryNameValueCollection.Remove(SharePointContext.SPAppWebUrlKey)
        queryNameValueCollection.Remove(SharePointContext.SPLanguageKey)
        queryNameValueCollection.Remove(SharePointContext.SPClientTagKey)
        queryNameValueCollection.Remove(SharePointContext.SPProductNumberKey)

        ' 添加 SPHasRedirectedToSharePoint=1。
        queryNameValueCollection.Add(SPHasRedirectedToSharePointKey, "1")

        Dim returnUrlBuilder As New UriBuilder(requestUrl)
        returnUrlBuilder.Query = queryNameValueCollection.ToString()

        ' 插入 StandardTokens。
        Const StandardTokens As String = "{StandardTokens}"
        Dim returnUrlString As String = returnUrlBuilder.Uri.AbsoluteUri
        returnUrlString = returnUrlString.Insert(returnUrlString.IndexOf("?") + 1, StandardTokens + "&")

        ' 构造重定向 URL。
        Dim redirectUrlString As String = TokenHelper.GetAppContextTokenRequestUrl(spHostUrl.AbsoluteUri, Uri.EscapeDataString(returnUrlString))

        redirectUrl = New Uri(redirectUrlString, UriKind.Absolute)

        Return RedirectionStatus.ShouldRedirect
    End Function

    ''' <summary>
    ''' 检查是否必须重定向到 SharePoint 供用户进行身份验证。
    ''' </summary>
    ''' <param name="httpContext">HTTP 上下文。</param>
    ''' <param name="redirectUrl">如果状态为“ShouldRedirect”，则返回指向 SharePoint 的重定向 URL。如果状态为“Ok”或“CanNotRedirect”，则返回 <c>Null</c>。</param>
    ''' <returns>重定向状态。</returns>
    Public Shared Function CheckRedirectionStatus(httpContext As HttpContext, ByRef redirectUrl As Uri) As RedirectionStatus
        Return CheckRedirectionStatus(New HttpContextWrapper(httpContext), redirectUrl)
    End Function

    ''' <summary>
    ''' 使用指定的 HTTP 请求创建 SharePointContext 实例。
    ''' </summary>
    ''' <param name="httpRequest">HTTP 请求。</param>
    ''' <returns>SharePointContext 实例。如果出现错误，则返回 <c>Nothing</c>。</returns>
    Public Function CreateSharePointContext(httpRequest As HttpRequestBase) As SharePointContext
        If httpRequest Is Nothing Then
            Throw New ArgumentNullException("httpRequest")
        End If

        ' SPHostUrl
        Dim spHostUrl As Uri = SharePointContext.GetSPHostUrl(httpRequest)
        If spHostUrl Is Nothing Then
            Return Nothing
        End If

        ' SPAppWebUrl
        Dim spAppWebUrlString As String = TokenHelper.EnsureTrailingSlash(httpRequest.QueryString(SharePointContext.SPAppWebUrlKey))
        Dim spAppWebUrl As Uri = Nothing
        If Not Uri.TryCreate(spAppWebUrlString, UriKind.Absolute, spAppWebUrl) OrElse
           Not (spAppWebUrl.Scheme = Uri.UriSchemeHttp OrElse spAppWebUrl.Scheme = Uri.UriSchemeHttps) Then
            spAppWebUrl = Nothing
        End If

        ' SPLanguage
        Dim spLanguage As String = httpRequest.QueryString(SharePointContext.SPLanguageKey)
        If String.IsNullOrEmpty(spLanguage) Then
            Return Nothing
        End If

        ' SPClientTag
        Dim spClientTag As String = httpRequest.QueryString(SharePointContext.SPClientTagKey)
        If String.IsNullOrEmpty(spClientTag) Then
            Return Nothing
        End If

        ' SPProductNumber
        Dim spProductNumber As String = httpRequest.QueryString(SharePointContext.SPProductNumberKey)
        If String.IsNullOrEmpty(spProductNumber) Then
            Return Nothing
        End If

        Return CreateSharePointContext(spHostUrl, spAppWebUrl, spLanguage, spClientTag, spProductNumber, httpRequest)
    End Function

    ''' <summary>
    ''' 使用指定的 HTTP 请求创建 SharePointContext 实例。
    ''' </summary>
    ''' <param name="httpRequest">HTTP 请求。</param>
    ''' <returns>SharePointContext 实例。如果出现错误，则返回 <c>Nothing</c>。</returns>
    Public Function CreateSharePointContext(httpRequest As HttpRequest) As SharePointContext
        Return CreateSharePointContext(New HttpRequestWrapper(httpRequest))
    End Function

    ''' <summary>
    ''' 获取与指定的 HTTP 上下文关联的 SharePointContext 实例。
    ''' </summary>
    ''' <param name="httpContext">HTTP 上下文。</param>
    ''' <returns>SharePointContext 实例。如果未找到，并且无法创建新实例，则返回 <c>Nothing</c>。</returns>
    Public Function GetSharePointContext(httpContext As HttpContextBase) As SharePointContext
        If httpContext Is Nothing Then
            Throw New ArgumentNullException("httpContext")
        End If

        Dim spHostUrl As Uri = SharePointContext.GetSPHostUrl(httpContext.Request)
        If spHostUrl Is Nothing Then
            Return Nothing
        End If

        Dim spContext As SharePointContext = LoadSharePointContext(httpContext)

        If spContext Is Nothing Or Not ValidateSharePointContext(spContext, httpContext) Then
            spContext = CreateSharePointContext(httpContext.Request)

            If spContext IsNot Nothing Then
                SaveSharePointContext(spContext, httpContext)
            End If
        End If

        Return spContext
    End Function

    ''' <summary>
    ''' 获取与指定的 HTTP 上下文关联的 SharePointContext 实例。
    ''' </summary>
    ''' <param name="httpContext">HTTP 上下文。</param>
    ''' <returns>SharePointContext 实例。如果未找到，并且无法创建新实例，则返回 <c>Nothing</c>。</returns>
    Public Function GetSharePointContext(httpContext As HttpContext) As SharePointContext
        Return GetSharePointContext(New HttpContextWrapper(httpContext))
    End Function

    ''' <summary>
    ''' 创建 SharePointContext 实例。
    ''' </summary>
    ''' <param name="spHostUrl">SharePoint 宿主 URL。</param>
    ''' <param name="spAppWebUrl">SharePoint 应用程序网站 URL。</param>
    ''' <param name="spLanguage">SharePoint 语言。</param>
    ''' <param name="spClientTag">SharePoint 客户端标记。</param>
    ''' <param name="spProductNumber">SharePoint 产品编号。</param>
    ''' <param name="httpRequest">HTTP 请求。</param>
    ''' <returns>SharePointContext 实例。如果出现错误，则返回 <c>Nothing</c>。</returns>
    Protected MustOverride Function CreateSharePointContext(spHostUrl As Uri, spAppWebUrl As Uri, spLanguage As String, spClientTag As String, spProductNumber As String, httpRequest As HttpRequestBase) As SharePointContext

    ''' <summary>
    ''' 验证给定的 SharePointContext 是否能与指定的 HTTP 上下文一起使用。
    ''' </summary>
    ''' <param name="spContext">SharePointContext。</param>
    ''' <param name="httpContext">HTTP 上下文。</param>
    ''' <returns>如果给定的 SharePointContext 能与指定的 HTTP 上下文一起使用，则为 True。</returns>
    Protected MustOverride Function ValidateSharePointContext(spContext As SharePointContext, httpContext As HttpContextBase) As Boolean

    ''' <summary>
    ''' 加载与指定的 HTTP 上下文关联的 SharePointContext 实例。
    ''' </summary>
    ''' <param name="httpContext">HTTP 上下文。</param>
    ''' <returns>SharePointContext 实例。如果未找到，则返回 <c>Nothing</c>。</returns>
    Protected MustOverride Function LoadSharePointContext(httpContext As HttpContextBase) As SharePointContext

    ''' <summary>
    ''' 保存与指定的 HTTP 上下文关联的指定的 SharePointContext 实例。
    ''' 接受 <c>Nothing</c> 可用于清除与 HTTP 上下文关联的 SharePointContext 实例。
    ''' </summary>
    ''' <param name="spContext">要保存的 SharePointContext 实例，或 <c>Nothing</c>。</param>
    ''' <param name="httpContext">HTTP 上下文。</param>
    Protected MustOverride Sub SaveSharePointContext(spContext As SharePointContext, httpContext As HttpContextBase)
End Class

#Region "ACS"

''' <summary>
''' 采用 ACS 模式封装 SharePoint 中的所有信息。
''' </summary>
Public Class SharePointAcsContext
    Inherits SharePointContext
    Private ReadOnly m_contextToken As String
    Private ReadOnly m_contextTokenObj As SharePointContextToken

    ''' <summary>
    ''' 上下文标记。
    ''' </summary>
    Public ReadOnly Property ContextToken() As String
        Get
            Return If(Me.m_contextTokenObj.ValidTo > DateTime.UtcNow, Me.m_contextToken, Nothing)
        End Get
    End Property

    ''' <summary>
    ''' 上下文标记的“CacheKey”声明。
    ''' </summary>
    Public ReadOnly Property CacheKey() As String
        Get
            Return If(Me.m_contextTokenObj.ValidTo > DateTime.UtcNow, Me.m_contextTokenObj.CacheKey, Nothing)
        End Get
    End Property

    ''' <summary>
    ''' 上下文标记的“refreshtoken”声明。
    ''' </summary>
    Public ReadOnly Property RefreshToken() As String
        Get
            Return If(Me.m_contextTokenObj.ValidTo > DateTime.UtcNow, Me.m_contextTokenObj.RefreshToken, Nothing)
        End Get
    End Property

    Public Overrides ReadOnly Property UserAccessTokenForSPHost() As String
        Get
            Return GetAccessTokenString(Me.m_userAccessTokenForSPHost, Function() TokenHelper.GetAccessToken(Me.m_contextTokenObj, Me.SPHostUrl.Authority))
        End Get
    End Property

    Public Overrides ReadOnly Property UserAccessTokenForSPAppWeb() As String
        Get
            If Me.SPAppWebUrl Is Nothing Then
                Return Nothing
            End If

            Return GetAccessTokenString(Me.m_userAccessTokenForSPAppWeb, Function() TokenHelper.GetAccessToken(Me.m_contextTokenObj, Me.SPAppWebUrl.Authority))
        End Get
    End Property

    Public Overrides ReadOnly Property AppOnlyAccessTokenForSPHost() As String
        Get
            Return GetAccessTokenString(Me.m_appOnlyAccessTokenForSPHost, Function() TokenHelper.GetAppOnlyAccessToken(TokenHelper.SharePointPrincipal, Me.SPHostUrl.Authority, TokenHelper.GetRealmFromTargetUrl(Me.SPHostUrl)))
        End Get
    End Property

    Public Overrides ReadOnly Property AppOnlyAccessTokenForSPAppWeb() As String
        Get
            If Me.SPAppWebUrl Is Nothing Then
                Return Nothing
            End If

            Return GetAccessTokenString(Me.m_appOnlyAccessTokenForSPAppWeb, Function() TokenHelper.GetAppOnlyAccessToken(TokenHelper.SharePointPrincipal, Me.SPAppWebUrl.Authority, TokenHelper.GetRealmFromTargetUrl(Me.SPAppWebUrl)))
        End Get
    End Property

    Public Sub New(spHostUrl As Uri, spAppWebUrl As Uri, spLanguage As String, spClientTag As String, spProductNumber As String, contextToken As String, contextTokenObj As SharePointContextToken)
        MyBase.New(spHostUrl, spAppWebUrl, spLanguage, spClientTag, spProductNumber)
        If String.IsNullOrEmpty(contextToken) Then
            Throw New ArgumentNullException("contextToken")
        End If

        If contextTokenObj Is Nothing Then
            Throw New ArgumentNullException("contextTokenObj")
        End If

        Me.m_contextToken = contextToken
        Me.m_contextTokenObj = contextTokenObj
    End Sub

    ''' <summary>
    ''' 确保访问令牌有效并返回该令牌。
    ''' </summary>
    ''' <param name="accessToken">要验证的访问令牌。</param>
    ''' <param name="tokenRenewalHandler">标记续订处理程序。</param>
    ''' <returns>访问令牌字符串。</returns>
    Private Shared Function GetAccessTokenString(ByRef accessToken As Tuple(Of String, Long), tokenRenewalHandler As Func(Of OAuthTokenResponse)) As String
        RenewAccessTokenIfNeeded(accessToken, tokenRenewalHandler)

        Return If(IsAccessTokenValid(accessToken), accessToken.Item1, Nothing)
    End Function

    ''' <summary>
    ''' 如果访问令牌无效，则应续订访问令牌。
    ''' </summary>
    ''' <param name="accessToken">要续订的访问令牌。</param>
    ''' <param name="tokenRenewalHandler">标记续订处理程序。</param>
    Private Shared Sub RenewAccessTokenIfNeeded(ByRef accessToken As Tuple(Of String, Long), tokenRenewalHandler As Func(Of OAuthTokenResponse))
        If IsAccessTokenValid(accessToken) Then
            Return
        End If

        Try
            Dim oauthTokenResponse As OAuthTokenResponse = tokenRenewalHandler()

            Dim expiresOn As Long = oauthTokenResponse.ExpiresOn

            If (expiresOn - oauthTokenResponse.NotBefore) > AccessTokenLifetimeTolerance Then
                ' 在访问令牌到期之前稍微提前一些进行续订
                ' 以便使用它的对 SharePoint 的调用将有足够的时间来成功完成操作。
                expiresOn -= AccessTokenLifetimeTolerance
            End If

            accessToken = Tuple.Create(oauthTokenResponse.AccessToken, expiresOn)
        Catch ex As WebException
        End Try
    End Sub
End Class

''' <summary>
''' SharePointAcsContext 的默认提供程序。
''' </summary>
Public Class SharePointAcsContextProvider
    Inherits SharePointContextProvider
    Private Const SPContextKey As String = "SPContext"
    Private Const SPCacheKeyKey As String = "SPCacheKey"

    Protected Overrides Function CreateSharePointContext(spHostUrl As Uri, spAppWebUrl As Uri, spLanguage As String, spClientTag As String, spProductNumber As String, httpRequest As HttpRequestBase) As SharePointContext
        Dim contextTokenString As String = TokenHelper.GetContextTokenFromRequest(httpRequest)
        If String.IsNullOrEmpty(contextTokenString) Then
            Return Nothing
        End If

        Dim contextToken As SharePointContextToken = Nothing
        Try
            contextToken = TokenHelper.ReadAndValidateContextToken(contextTokenString, httpRequest.Url.Authority)
        Catch ex As WebException
            Return Nothing
        Catch ex As AudienceUriValidationFailedException
            Return Nothing
        End Try

        Return New SharePointAcsContext(spHostUrl, spAppWebUrl, spLanguage, spClientTag, spProductNumber, contextTokenString, contextToken)
    End Function

    Protected Overrides Function ValidateSharePointContext(spContext As SharePointContext, httpContext As HttpContextBase) As Boolean
        Dim spAcsContext As SharePointAcsContext = TryCast(spContext, SharePointAcsContext)

        If spAcsContext IsNot Nothing Then
            Dim spHostUrl As Uri = SharePointContext.GetSPHostUrl(httpContext.Request)
            Dim contextToken As String = TokenHelper.GetContextTokenFromRequest(httpContext.Request)
            Dim spCacheKeyCookie As HttpCookie = httpContext.Request.Cookies(SPCacheKeyKey)
            Dim spCacheKey As String = If(spCacheKeyCookie IsNot Nothing, spCacheKeyCookie.Value, Nothing)

            Return spHostUrl = spAcsContext.SPHostUrl AndAlso
                   Not String.IsNullOrEmpty(spAcsContext.CacheKey) AndAlso
                   spCacheKey = spAcsContext.CacheKey AndAlso
                   Not String.IsNullOrEmpty(spAcsContext.ContextToken) AndAlso
                   (String.IsNullOrEmpty(contextToken) OrElse contextToken = spAcsContext.ContextToken)
        End If

        Return False
    End Function

    Protected Overrides Function LoadSharePointContext(httpContext As HttpContextBase) As SharePointContext
        Return TryCast(httpContext.Session(SPContextKey), SharePointAcsContext)
    End Function

    Protected Overrides Sub SaveSharePointContext(spContext As SharePointContext, httpContext As HttpContextBase)
        Dim spAcsContext As SharePointAcsContext = TryCast(spContext, SharePointAcsContext)

        If spAcsContext IsNot Nothing Then
            Dim spCacheKeyCookie As New HttpCookie(SPCacheKeyKey) With
            {
                .Value = spAcsContext.CacheKey,
                .Secure = True,
                .HttpOnly = True
            }

            httpContext.Response.AppendCookie(spCacheKeyCookie)
        End If

        httpContext.Session(SPContextKey) = spAcsContext
    End Sub
End Class

#End Region

#Region "HighTrust"

''' <summary>
''' 采用 HighTrust 模式封装 SharePoint 中的所有信息。
''' </summary>
Public Class SharePointHighTrustContext
    Inherits SharePointContext
    Private ReadOnly m_logonUserIdentity As WindowsIdentity

    ''' <summary>
    ''' 当前用户的 Windows 标识。
    ''' </summary>
    Public ReadOnly Property LogonUserIdentity() As WindowsIdentity
        Get
            Return Me.m_logonUserIdentity
        End Get
    End Property

    Public Overrides ReadOnly Property UserAccessTokenForSPHost() As String
        Get
            Return GetAccessTokenString(Me.m_userAccessTokenForSPHost, Function() TokenHelper.GetS2SAccessTokenWithWindowsIdentity(Me.SPHostUrl, Me.LogonUserIdentity))
        End Get
    End Property

    Public Overrides ReadOnly Property UserAccessTokenForSPAppWeb() As String
        Get
            If Me.SPAppWebUrl Is Nothing Then
                Return Nothing
            End If

            Return GetAccessTokenString(Me.m_userAccessTokenForSPAppWeb, Function() TokenHelper.GetS2SAccessTokenWithWindowsIdentity(Me.SPAppWebUrl, Me.LogonUserIdentity))
        End Get
    End Property

    Public Overrides ReadOnly Property AppOnlyAccessTokenForSPHost() As String
        Get
            Return GetAccessTokenString(Me.m_appOnlyAccessTokenForSPHost, Function() TokenHelper.GetS2SAccessTokenWithWindowsIdentity(Me.SPHostUrl, Nothing))
        End Get
    End Property

    Public Overrides ReadOnly Property AppOnlyAccessTokenForSPAppWeb() As String
        Get
            If Me.SPAppWebUrl Is Nothing Then
                Return Nothing
            End If

            Return GetAccessTokenString(Me.m_appOnlyAccessTokenForSPAppWeb, Function() TokenHelper.GetS2SAccessTokenWithWindowsIdentity(Me.SPAppWebUrl, Nothing))
        End Get
    End Property

    Public Sub New(spHostUrl As Uri, spAppWebUrl As Uri, spLanguage As String, spClientTag As String, spProductNumber As String, logonUserIdentity As WindowsIdentity)
        MyBase.New(spHostUrl, spAppWebUrl, spLanguage, spClientTag, spProductNumber)
        If logonUserIdentity Is Nothing Then
            Throw New ArgumentNullException("logonUserIdentity")
        End If

        Me.m_logonUserIdentity = logonUserIdentity
    End Sub

    ''' <summary>
    ''' 确保访问令牌有效并返回该令牌。
    ''' </summary>
    ''' <param name="accessToken">要验证的访问令牌。</param>
    ''' <param name="tokenRenewalHandler">标记续订处理程序。</param>
    ''' <returns>访问令牌字符串。</returns>
    Private Shared Function GetAccessTokenString(ByRef accessToken As Tuple(Of String, Long), tokenRenewalHandler As Func(Of String)) As String
        RenewAccessTokenIfNeeded(accessToken, tokenRenewalHandler)

        Return If(IsAccessTokenValid(accessToken), accessToken.Item1, Nothing)
    End Function

    ''' <summary>
    ''' 如果访问令牌无效，则应续订访问令牌。
    ''' </summary>
    ''' <param name="accessToken">要续订的访问令牌。</param>
    ''' <param name="tokenRenewalHandler">标记续订处理程序。</param>
    Private Shared Sub RenewAccessTokenIfNeeded(ByRef accessToken As Tuple(Of String, Long), tokenRenewalHandler As Func(Of String))
        If IsAccessTokenValid(accessToken) Then
            Return
        End If

        Dim expiresOn As Long = TokenHelper.EpochTimeNow() + TokenHelper.HighTrustAccessTokenLifetime.TotalSeconds

        If TokenHelper.HighTrustAccessTokenLifetime.TotalSeconds > AccessTokenLifetimeTolerance Then
            ' 在访问令牌到期之前稍微提前一些进行续订
            ' 以便使用它的对 SharePoint 的调用将有足够的时间来成功完成操作。
            expiresOn -= AccessTokenLifetimeTolerance
        End If

        accessToken = Tuple.Create(tokenRenewalHandler(), expiresOn)
    End Sub
End Class

''' <summary>
''' SharePointHighTrustContext 的默认提供程序。
''' </summary>
Public Class SharePointHighTrustContextProvider
    Inherits SharePointContextProvider
    Private Const SPContextKey As String = "SPContext"

    Protected Overrides Function CreateSharePointContext(spHostUrl As Uri, spAppWebUrl As Uri, spLanguage As String, spClientTag As String, spProductNumber As String, httpRequest As HttpRequestBase) As SharePointContext
        Dim logonUserIdentity As WindowsIdentity = httpRequest.LogonUserIdentity
        If logonUserIdentity Is Nothing Or Not logonUserIdentity.IsAuthenticated Or logonUserIdentity.IsGuest Or logonUserIdentity.User Is Nothing Then
            Return Nothing
        End If

        Return New SharePointHighTrustContext(spHostUrl, spAppWebUrl, spLanguage, spClientTag, spProductNumber, logonUserIdentity)
    End Function

    Protected Overrides Function ValidateSharePointContext(spContext As SharePointContext, httpContext As HttpContextBase) As Boolean
        Dim spHighTrustContext As SharePointHighTrustContext = TryCast(spContext, SharePointHighTrustContext)

        If spHighTrustContext IsNot Nothing Then
            Dim spHostUrl As Uri = SharePointContext.GetSPHostUrl(httpContext.Request)
            Dim logonUserIdentity As WindowsIdentity = httpContext.Request.LogonUserIdentity

            Return spHostUrl = spHighTrustContext.SPHostUrl AndAlso
                   logonUserIdentity IsNot Nothing AndAlso
                   logonUserIdentity.IsAuthenticated AndAlso
                   Not logonUserIdentity.IsGuest AndAlso
                   logonUserIdentity.User = spHighTrustContext.LogonUserIdentity.User
        End If

        Return False
    End Function

    Protected Overrides Function LoadSharePointContext(httpContext As HttpContextBase) As SharePointContext
        Return TryCast(httpContext.Session(SPContextKey), SharePointHighTrustContext)
    End Function

    Protected Overrides Sub SaveSharePointContext(spContext As SharePointContext, httpContext As HttpContextBase)
        httpContext.Session(SPContextKey) = TryCast(spContext, SharePointHighTrustContext)
    End Sub
End Class

#End Region
