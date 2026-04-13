
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

    public async Task PromptingInterfaceDelegate(HttpContext context)
    {
        // "using" is a C# system to ensure that the object is disposed of properly
        // when the block is exited. In this case, it will call the Dispose method
        using(var log = _logger.StartMethod(nameof(PromptingInterfaceDelegate), context))
        {
            try
        {
            string inputText = "";

            // Prefer form data (from HTML form)
            if (context.Request.HasFormContentType)
            {
                inputText = context.Request.Form["text"].ToString().Trim();
            }
            // Fallback: query string (for direct curl testing)
            else if (context.Request.Query.TryGetValue("text", out var queryValues))
            {
                inputText = queryValues.ToString().Trim();
            }
            // Fallback: raw body
            else if (context.Request.ContentLength > 0)
            {
                using var reader = new StreamReader(context.Request.Body);
                inputText = (await reader.ReadToEndAsync()).Trim();
            }

            if (string.IsNullOrWhiteSpace(inputText))
            {
                throw new UserErrorException("No text provided");
            }

            log.SetAttribute("input.text", inputText);
            log.SetAttribute("input.length", inputText.Length);

            // Call your processing function
            //await ProcessUserTextInput(inputText, log);

            // Return nice JSON
            var responseObj = new
            {
                status = "success",
                message = "Text received and processed",
                textLength = inputText.Length,
                receivedTextPreview = inputText.Length > 100 
                    ? inputText.Substring(0, 100) + "..." 
                    : inputText
            };

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(responseObj));
        }
        catch (Exception e)
        {
            log.HandleException(e);
        }
        }
    }
}