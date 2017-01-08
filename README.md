# OAuth2.Demos.AuthzCodeGrant #

Yet another OAuth 2.0 Authorization Code Grant (Web flow) demo.

It is based on a .NET console application that runs a self-hosted ASP.NET Web API server to simulate the Web server.

By default, it uses the GitHub Authorization Server and the Github API as a Resource Server.
However, this can easily be changed on the configuration.

In order to understand this OAuth 2.0 flow, we suggest:

* Reading the source code, which is all in one file
* Looking to at exchanged messages, using a tool such as Fiddler
* Reading the console messages.

> Note: You are likely to receive an error when creating and opening the `HttpSelfHostServer` stating
> that you're not permitted to create a reservation for the callback url http://localhost/callback. This
> issue is easily remedied by reserving this URL [using these instructions](https://msdn.microsoft.com/en-us/library/ms733768(v=vs.110).aspx).

Any feedback is greatly appreciated.
