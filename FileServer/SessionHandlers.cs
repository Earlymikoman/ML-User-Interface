
using AzureFileServer.Azure;
using AzureFileServer.Utils;
using Microsoft.Extensions.Primitives;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AzureFileServer.FileServer;

// This is the core logic of the web server and hosts all of the HTTP
// handlers used by the web server regarding File Server functionality.
public class Sessions
{
    private readonly IConfiguration _configuration;
    private readonly Logger _logger;
    private readonly CosmosDbWrapper _cosmosDbWrapper;
    private readonly IHttpClientFactory _httpClientFactory;

    //I'm realizing this whole class is magic; something is filling in these parameters, according to Claude.
    public Sessions(IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        _configuration = configuration;
        if (null == _configuration)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        string serviceName = configuration["Logging:ServiceName"];
        _logger = new Logger(serviceName);

        _cosmosDbWrapper = new CosmosDbWrapper(configuration);
        _httpClientFactory = httpClientFactory;
    }

    private static string GetParameterFromList(string parameterName, HttpRequest request, MethodLogger log)
    {
        // Obtain the parameter from the caller
        if (request.Query.TryGetValue(parameterName, out StringValues items))
        {
            if (items.Count > 1)
            {
                throw new UserErrorException($"Multiple {parameterName} found");
            }

            log.SetAttribute($"request.{parameterName}", items[0]);
        }
        else
        {
            throw new UserErrorException($"No {parameterName} found");
        }

        return items[0];
    }

    public async Task DefaultDelegate(HttpContext context)
    {
        // "using" is a C# system to ensure that the object is disposed of properly
        // when the block is exited. In this case, it will call the Dispose method
        using(var log = _logger.StartMethod(nameof(DefaultDelegate), context))
        {
            try
            {
                // Generally, a 200 OK is returned if the service is alive
                // and that is all that the load balancer needs, but a
                // text message can be useful for humans.
                // However, in some cases, the LB will be able to process more
                // health information to know how to react to your service, so
                // don't be surprised if you see code with more involved health 
                // checks.
                await context.Response.WriteAsync("Default for ml-user-interface");
            }
            catch(Exception e)
            {
                // While you can just throw the exception back to the web server,
                // it is not recommended. It is better to catch the exception and
                // log it, then return a 500 Internal Server Error to the caller yourself.
                log.HandleException(e);
            }
        }
    }

    // Health Checks (aka ping) methods are handy to have on your service
    // They allow you to report that your are alive and return any other
    // information that is useful. These are often used by load balancers
    // to decide whether to send you traffic. For example, if you need a long
    // time to initialize, you can report that you are not ready yet.
    public async Task HealthCheckDelegate(HttpContext context)
    {
        // "using" is a C# system to ensure that the object is disposed of properly
        // when the block is exited. In this case, it will call the Dispose method
        using(var log = _logger.StartMethod(nameof(HealthCheckDelegate), context))
        {
            try
            {
                // Generally, a 200 OK is returned if the service is alive
                // and that is all that the load balancer needs, but a
                // text message can be useful for humans.
                // However, in some cases, the LB will be able to process more
                // health information to know how to react to your service, so
                // don't be surprised if you see code with more involved health 
                // checks.
                await context.Response.WriteAsync("Alive");
            }
            catch(Exception e)
            {
                // While you can just throw the exception back to the web server,
                // it is not recommended. It is better to catch the exception and
                // log it, then return a 500 Internal Server Error to the caller yourself.
                log.HandleException(e);
            }
        }
    }

    public async Task LoginDelegate(HttpContext context)
    {
        // "using" is a C# system to ensure that the object is disposed of properly
        // when the block is exited. In this case, it will call the Dispose method
        using(var log = _logger.StartMethod(nameof(LoginDelegate), context))
        {
            try
            {
                HttpRequest request = context.Request;
                HttpResponse response = context.Response;

                string userid = context.Request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"].FirstOrDefault();

                string sessionUrl =
                _configuration["AzureFileServer:ConnectionStrings:SessionManagerEndpoint"] + "/login?userid=" + userid;
                log.SetAttribute("request.url", sessionUrl);

                var sessionClient = _httpClientFactory.CreateClient();
                var sessionResponse = await sessionClient.GetAsync(sessionUrl);

                if (!sessionResponse.IsSuccessStatusCode)
                {
                    var error = await sessionResponse.Content.ReadAsStringAsync();
                    log.SetAttribute("downstream.error", $"{(int)sessionResponse.StatusCode} {sessionResponse.ReasonPhrase}");
                    throw new UserErrorException($"Forward failed: {(int)sessionResponse.StatusCode}");
                }



                var CurrentSessionData = new 
                    { 
                        User = userid, 
                        PromptType = "", 
                        PromptName = "" 
                    };
                    string sessionJson = JsonSerializer.Serialize(CurrentSessionData);

                    //Grok knows its cookies
                    var cookieOptions = new CookieOptions
                    {
                        Expires = DateTimeOffset.UtcNow.AddDays(1),   // or .AddHours(1), etc.
                        HttpOnly = true,                              // Prevents JavaScript access (security)
                        Secure = true,                                // Only send over HTTPS
                        IsEssential = true,                           // For GDPR consent (if needed)
                        SameSite = SameSiteMode.Strict                // or Lax / None
                    };

                    response.Cookies.Append("CurrentSessionData", sessionJson, cookieOptions);

                    response.StatusCode = 200;
                    response.ContentLength = Encoding.UTF8.GetByteCount("");
                    response.ContentType = "text/plain; charset=utf-8";

                    await using (var bodyWriter = new StreamWriter(response.Body, leaveOpen: true))
                    {
                        await bodyWriter.WriteAsync("");
                        await bodyWriter.FlushAsync();
                    }

                log.SetAttribute("response.contenttype", response.ContentType);
                log.SetAttribute("response.contentlength", response.ContentLength);
                log.SetAttribute("response.content", response.Body);
            }
            catch(Exception e)
            {
                // While you can just throw the exception back to the web server,
                // it is not recommended. It is better to catch the exception and
                // log it, then return a 500 Internal Server Error to the caller yourself.
                log.HandleException(e);
            }
        }
    }
    public async Task CachePromptTypeDelegate(HttpContext context)
    {
        // "using" is a C# system to ensure that the object is disposed of properly
        // when the block is exited. In this case, it will call the Dispose method
        using(var log = _logger.StartMethod(nameof(CachePromptTypeDelegate), context))
        {
            try
            {
                HttpRequest request = context.Request;
                HttpResponse response = context.Response;

                string prompttype = GetParameterFromList("prompttype", request, log);

                SessionData sessionData = new SessionData();
                var cookieValue = request.Cookies["CurrentSessionData"];
                if (string.IsNullOrEmpty(cookieValue))
                {
                    throw new UserErrorException("No Session Data Found");
                }
                sessionData = JsonSerializer.Deserialize<SessionData>(cookieValue);

                var CurrentSessionData = new 
                    { 
                        User = sessionData.User, 
                        PromptType = prompttype, 
                        PromptName = sessionData.PromptName 
                    };
                    string sessionJson = JsonSerializer.Serialize(CurrentSessionData);

                    //Grok knows its cookies
                    var cookieOptions = new CookieOptions
                    {
                        Expires = DateTimeOffset.UtcNow.AddDays(1),   // or .AddHours(1), etc.
                        HttpOnly = true,                              // Prevents JavaScript access (security)
                        Secure = true,                                // Only send over HTTPS
                        IsEssential = true,                           // For GDPR consent (if needed)
                        SameSite = SameSiteMode.Strict                // or Lax / None
                    };

                    response.Cookies.Append("CurrentSessionData", sessionJson, cookieOptions);

                    response.StatusCode = 200;
                    response.ContentLength = Encoding.UTF8.GetByteCount("");
                    response.ContentType = "text/plain; charset=utf-8";

                    await using (var bodyWriter = new StreamWriter(response.Body, leaveOpen: true))
                    {
                        await bodyWriter.WriteAsync("");
                        await bodyWriter.FlushAsync();
                    }

                log.SetAttribute("response.contenttype", response.ContentType);
                log.SetAttribute("response.contentlength", response.ContentLength);
                log.SetAttribute("response.content", response.Body);
            }
            catch(Exception e)
            {
                // While you can just throw the exception back to the web server,
                // it is not recommended. It is better to catch the exception and
                // log it, then return a 500 Internal Server Error to the caller yourself.
                log.HandleException(e);
            }
        }
    }

    public async Task WritePromptResponseDelegate(HttpContext context)
    {
        using(var log = _logger.StartMethod(nameof(WritePromptResponseDelegate), context))
        {
            try
            {
                HttpRequest request = context.Request;

                IFormFile fileContent = context.Request.Form.Files.FirstOrDefault();
                if (fileContent == null)
                {
                    throw new UserErrorException("No file content found");
                }
                string fileString;
                using (var reader = new StreamReader(fileContent.OpenReadStream()))
                {
                    fileString = await reader.ReadToEndAsync();
                }



                UserMetadata m = new UserMetadata();
                SessionData sessionData = new SessionData();
                var cookieValue = request.Cookies["CurrentSessionData"];
                if (string.IsNullOrEmpty(cookieValue))
                {
                    throw new UserErrorException("No Session Data Found");
                }
                sessionData = JsonSerializer.Deserialize<SessionData>(cookieValue);

                //m.userid = context.Request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"].FirstOrDefault();
                m.userid = sessionData.User;
                m.prompttype = sessionData.PromptType;
                string promptname = sessionData.PromptName;

                log.SetAttribute("cookies.userid", m.userid);
                log.SetAttribute("cookies.prompttype", m.prompttype);
                log.SetAttribute("cookies.promptname", promptname);



                string sessionUrl =
                _configuration["AzureFileServer:ConnectionStrings:SessionManagerEndpoint"] + "/writepromptresponse?userid=" + m.userid + "&prompttype=" + m.prompttype + "&promptname=" + promptname;
                log.SetAttribute("request.url", sessionUrl);

                

                //Grok showing me how to attach a file programmatically.
                // Create the multipart form data (replicates -F)
                var multipartContent = new MultipartFormDataContent();

                // Convert your string to bytes and add it as a file field (replicates file=@FILENAME.EXT)
                var newFileContent = new StringContent(fileString);
                newFileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
                multipartContent.Add(newFileContent, "file", "dummy.txt");



                var sessionClient = _httpClientFactory.CreateClient();
                var response = await sessionClient.PostAsync(sessionUrl, multipartContent);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    log.SetAttribute("downstream.error", $"{(int)response.StatusCode} {response.ReasonPhrase}");
                    throw new UserErrorException($"Forward failed: {(int)response.StatusCode}");
                }

                // The POST has no response body, so we just return and the system
                // will return a 200 OK to the caller.
            }
            catch (UserErrorException e)
            {
                log.LogUserError(e.Message);
            }
            catch(Exception e)
            {
                log.HandleException(e);
            }
        }
    }

    //Complete
    public async Task AcquirePromptDelegate(HttpContext context)
    {
        using(var log = _logger.StartMethod(nameof(AcquirePromptDelegate), context))
        {
            try
            {
                HttpRequest request = context.Request;
                HttpResponse response = context.Response;

                UserMetadata m = new UserMetadata();
                SessionData sessionData = new SessionData();
                var cookieValue = request.Cookies["CurrentSessionData"];
                if (string.IsNullOrEmpty(cookieValue))
                {
                    throw new UserErrorException("No Session Data Found");
                }
                sessionData = JsonSerializer.Deserialize<SessionData>(cookieValue);

                m.userid = sessionData.User;
                m.prompttype = sessionData.PromptType;

                log.SetAttribute("cookies.userid", m.userid);
                log.SetAttribute("cookies.prompttype", m.prompttype);

                

                string sessionUrl = _configuration["AzureFileServer:ConnectionStrings:SessionManagerEndpoint"] + "/acquireprompt?userid=" + m.userid + "&prompttype=" + m.prompttype;
                log.SetAttribute("request.url", sessionUrl);

                var sessionClient = _httpClientFactory.CreateClient();
                var sessionResponse = await sessionClient.GetAsync(sessionUrl);

                if (!sessionResponse.IsSuccessStatusCode)
                {
                    throw new UserErrorException();
                }

                var sessionContent = await sessionResponse.Content.ReadAsStringAsync();
                var promptData = JsonSerializer.Deserialize<Dictionary<string, string>>(sessionContent);

                var CurrentSessionData = new 
                    { 
                        User = m.userid, 
                        PromptType = m.prompttype, 
                        PromptName = promptData["promptname"] 
                    };
                    string sessionJson = JsonSerializer.Serialize(CurrentSessionData);

                    //Grok knows its cookies
                    var cookieOptions = new CookieOptions
                    {
                        Expires = DateTimeOffset.UtcNow.AddDays(1),   // or .AddHours(1), etc.
                        HttpOnly = true,                              // Prevents JavaScript access (security)
                        Secure = true,                                // Only send over HTTPS
                        IsEssential = true,                           // For GDPR consent (if needed)
                        SameSite = SameSiteMode.Strict                // or Lax / None
                    };

                    response.Cookies.Append("CurrentSessionData", sessionJson, cookieOptions);

                response.StatusCode = 200;
                response.ContentLength = Encoding.UTF8.GetByteCount(promptData["promptcontent"]);
                response.ContentType = "text/plain; charset=utf-8";

                await using (var bodyWriter = new StreamWriter(response.Body, leaveOpen: true))
                {
                    await bodyWriter.WriteAsync(promptData["promptcontent"]);
                    await bodyWriter.FlushAsync();
                }

                log.SetAttribute("response.contenttype", response.ContentType);
                log.SetAttribute("response.contentlength", response.ContentLength);
                log.SetAttribute("response.content", response.Body);
            }
            catch (UserErrorException e)
            {
                log.LogUserError(e.Message);
            }
            catch(Exception e)
            {
                log.HandleException(e);
            }
        }
    }

    public async Task SimpleTextInputDelegate(HttpContext context)
    {
        using (var log = _logger.StartMethod(nameof(SimpleTextInputDelegate), context))
        {
            try
            {
                string inputText = "";

                // This is the correct way to read form data from an HTML <form> submission
                if (context.Request.HasFormContentType)
                {
                    var form = await context.Request.ReadFormAsync();
                    inputText = form["text"].ToString().Trim();   // No ? needed - indexer returns StringValues.Empty if missing
                }
                // Fallback for testing with curl or direct URL
                else if (context.Request.Query.TryGetValue("text", out var queryValues))
                {
                    inputText = queryValues.ToString().Trim();
                }

                if (string.IsNullOrWhiteSpace(inputText))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync(
                        JsonSerializer.Serialize(new { error = "Please enter some text in the box." }));
                    return;
                }

                log.SetAttribute("input.text", inputText);
                log.SetAttribute("input.length", inputText.Length);

                // Call your processing function here
                //await ProcessUserTextInput(inputText, log);

                // Return success
                var result = new
                {
                    status = "success",
                    message = "Text received successfully",
                    text = inputText,
                    length = inputText.Length
                };

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync(JsonSerializer.Serialize(result));
            }
            catch (Exception ex)
            {
                log.HandleException(ex);
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync(
                    JsonSerializer.Serialize(new { error = "An error occurred on the server" }));
            }
        }
    }
}