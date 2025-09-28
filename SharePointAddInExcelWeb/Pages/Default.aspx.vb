﻿Public Class _Default
    Inherits System.Web.UI.Page

    Protected Sub Page_PreInit(sender As Object, ByVal e As EventArgs) Handles Me.PreInit
        Dim redirectUrl As Uri = Nothing
        Select Case SharePointContextProvider.CheckRedirectionStatus(Context, redirectUrl)
            Case RedirectionStatus.Ok
                Return
            Case RedirectionStatus.ShouldRedirect
                Response.Redirect(redirectUrl.AbsoluteUri, endResponse:=True)
                Exit Select
            Case RedirectionStatus.CanNotRedirect
                Response.Write("An error occurred while processing your request.")
                Response.End()
                Exit Select
        End Select
    End Sub

    Protected Sub Page_Load(ByVal sender As Object, ByVal e As EventArgs) Handles Me.Load
        ' 以下代码使用 TokenHelper 获取客户端上下文和“标题”属性。 
        ' 若要访问其他属性，应用可能需要请求对主机 Web 的权限。
        Dim spContext = SharePointContextProvider.Current.GetSharePointContext(Context)

        Using clientContext = spContext.CreateUserClientContextForSPHost()
            clientContext.Load(clientContext.Web, Function(web) web.Title)
            clientContext.ExecuteQuery()
            Response.Write(clientContext.Web.Title)
        End Using
    End Sub
End Class