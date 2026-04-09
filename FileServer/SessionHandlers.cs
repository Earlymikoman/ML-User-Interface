
using AzureFileServer.Azure;
using AzureFileServer.Utils;
using Microsoft.Extensions.Primitives;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Net.Http.Headers;

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
                await context.Response.WriteAsync("Default for ml-session-manager");
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
        using(var log = _logger.StartMethod(nameof(LoginDelegate), context))
        {
            try
            {
                HttpRequest request = context.Request;

                UserMetadata m = new UserMetadata();
                m.userid = GetParameterFromList("userid", request, log);
                m.prompttype = GetParameterFromList("prompttype", request, log);

                log.SetAttribute("request.userid", m.userid);
                log.SetAttribute("request.prompttype", m.prompttype);

                // First step is we will write the metadata to CosmosDB
                // Here we are using Type mapping to convert our data structure
                // to a JSON document that can be stored in CosmosDB.
                if (await _cosmosDbWrapper.GetItemAsync<UserMetadata>(m.id, m.userid) == null)
                {
                    await _cosmosDbWrapper.AddItemAsync(m, m.userid);
                }

                // The POST has no response body, so we just return and the system
                // will return a 200 OK to the caller.
            }
            catch(Exception e)
            {
                log.HandleException(e);
            }
        }
    }
    public async Task GetSessionDataDelegate(HttpContext context)
    {
        using(var log = _logger.StartMethod(nameof(GetSessionDataDelegate), context))
        {
            try
            {
                string responseString = "";

                HttpResponse response = context.Response;

                response.StatusCode = 200;
                response.ContentLength = Encoding.UTF8.GetByteCount(responseString);
                response.ContentType = "text/plain; charset=utf-8";

                await using (var bodyWriter = new StreamWriter(response.Body, leaveOpen: true))
                {
                    await bodyWriter.WriteAsync(responseString);
                    await bodyWriter.FlushAsync();
                }

                log.SetAttribute("response.contenttype", response.ContentType);
                log.SetAttribute("response.contentlength", response.ContentLength);
                log.SetAttribute("response.content", response.Body);
            }
            catch(Exception e)
            {
                log.HandleException(e);
            }
        }
    }

    //Incomplete
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

                UserMetadata m = new UserMetadata();
                m.userid = GetParameterFromList("userid", request, log);
                m.prompttype = GetParameterFromList("prompttype", request, log);
                string sourceprompt = m.prompttype + "-" + GetParameterFromList("promptname", request, log);

                //m.filename = Path.ChangeExtension(Path.GetFileNameWithoutExtension(m.filename), Path.GetExtension(m.filename).ToLowerInvariant());               

                string dataUrl = _configuration["AzureFileServer:ConnectionStrings:DataHandlerEndpoint"] + "/uploaddata?userid=" + m.userid + "&sourceprompt=" + sourceprompt;
                log.SetAttribute("request.url", dataUrl);

                var dataClient = _httpClientFactory.CreateClient();


                //Grok showing me how to attach a file programmatically.
                using var formContent = new MultipartFormDataContent();

                // Reuse the original file stream directly (no extra MemoryStream or byte[] copy)
                var fileStreamContent = new StreamContent(fileContent.OpenReadStream());
                fileStreamContent.Headers.ContentType = MediaTypeHeaderValue.Parse(
                    fileContent.ContentType ?? "application/octet-stream");

                formContent.Add(fileStreamContent, "file", fileContent.FileName);   // field name = "file"

                var response = await dataClient.PostAsync(dataUrl, formContent);

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

                UserMetadata m = new UserMetadata();
                m.userid = GetParameterFromList("userid", request, log);
                m.prompttype = GetParameterFromList("prompttype", request, log);

                log.SetAttribute("request.userid", m.userid);
                log.SetAttribute("request.prompttype", m.prompttype);

                m = await _cosmosDbWrapper.GetItemAsync<UserMetadata>(m.id, m.userid);
                if (m == null)
                {
                    throw new UserErrorException();
                }



                string listUrl = _configuration["AzureFileServer:ConnectionStrings:PromptHandlerEndpoint"] + "/listprompts?prompttype=" + m.prompttype;
                log.SetAttribute("request.url", listUrl);

                var listClient = _httpClientFactory.CreateClient();
                var listResponse = await listClient.GetAsync
                (_configuration["AzureFileServer:ConnectionStrings:PromptHandlerEndpoint"] + "/listprompts?prompttype=" + m.prompttype);

                if (!listResponse.IsSuccessStatusCode)
                {
                    throw new UserErrorException();
                }

                var listContent = await listResponse.Content.ReadAsStringAsync();
                List<string> promptnames = new List<string>(){""};
                foreach (var character in listContent)
                {
                    if (character == '\n')
                    {
                        promptnames.Add("");

                        continue;
                    }

                    promptnames[promptnames.Count - 1] += character;
                }



                string responseString = "No New Prompts Found Of " + promptnames.Count + "Prompts.\n\n" + listContent;
                if (m.promptdepth + 1 < promptnames.Count - 1 && m.promptdepth >= 0)
                {
                    string promptToRequest = promptnames[m.promptdepth + 1];
                    responseString = "Couldn't Find Prompt: " + promptToRequest;

                    var promptClient = _httpClientFactory.CreateClient();
                    var promptResponse = await promptClient.GetAsync
                    (_configuration["AzureFileServer:ConnectionStrings:PromptHandlerEndpoint"] + "/getprompt?prompttype=" + m.prompttype + "&promptname=" + promptToRequest);

                    if (!promptResponse.IsSuccessStatusCode)
                    {
                        throw new UserErrorException();
                    }

                    var promptContent = await promptResponse.Content.ReadAsStringAsync();

                    responseString = promptContent;
                }



                HttpResponse response = context.Response;

                response.StatusCode = 200;
                response.ContentLength = Encoding.UTF8.GetByteCount(responseString);
                response.ContentType = "text/plain; charset=utf-8";

                await using (var bodyWriter = new StreamWriter(response.Body, leaveOpen: true))
                {
                    await bodyWriter.WriteAsync(responseString);
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

    //Incomplete
    // public async Task ListPromptResponsesDelegate(HttpContext context)
    // {
    //     using(var log = _logger.StartMethod(nameof(ListPromptResponsesDelegate), context))
    //     {
    //         try
    //         {
    //             HttpRequest request = context.Request;

    //             UserMetadata m = new UserMetadata();
    //             m.userid = GetParameterFromList("userid", request, log);

    //             // TODO: Implement the list files delegate to return a list of files
    //             // that are associated with the userId provided in the HTTP request.
    //             HttpResponse response = context.Response;
    //             string query = $"SELECT * FROM c WHERE c.userid = \"{m.userid}\"";
    //             IEnumerable<UserMetadata> metadatas = await _cosmosDbWrapper.GetItemsAsync<UserMetadata>(query);
    //             if (metadatas == null)
    //             {
    //                 throw new UserErrorException();
    //             }
                
    //             string fileStrings = metadatas.Count() + " Files Found:\n";
    //             foreach (UserMetadata metadata in metadatas)
    //             {
    //                 fileStrings += "\t" + metadata.ToString() + "\n";
    //             }
    //             response.StatusCode = 200;
    //             response.ContentLength = Encoding.UTF8.GetByteCount(fileStrings);
    //             response.ContentType = "text/plain; charset=utf-8";

    //             await using (var bodyWriter = new StreamWriter(response.Body, leaveOpen: true))
    //             {
    //                 await bodyWriter.WriteAsync(fileStrings);
    //                 await bodyWriter.FlushAsync();
    //             }

    //             log.SetAttribute("response.contenttype", response.ContentType);
    //             log.SetAttribute("response.contentlength", response.ContentLength);
    //             log.SetAttribute("response.content", response.Body);
    //         }
    //         catch (UserErrorException e)
    //         {
    //             log.LogUserError(e.Message);
    //         }
    //         catch(Exception e)
    //         {
    //             log.HandleException(e);
    //         }
    //     }
    // }

    //Incomplete
    // public async Task DeletePromptResponseDelegate(HttpContext context)
    // {
    //     using(var log = _logger.StartMethod(nameof(DeletePromptResponseDelegate), context))
    //     {
    //         try
    //         {
    //             HttpRequest request = context.Request;

    //             UserMetadata m = new UserMetadata();
    //             m.userid = GetParameterFromList("userid", request, log);
    //             m.filename = GetParameterFromList("filename", request, log);

    //             m.filename = Path.ChangeExtension(Path.GetFileNameWithoutExtension(m.filename), Path.GetExtension(m.filename).ToLowerInvariant());

    //             // TODO: Implement the delete file delegate to remove the file
    //             // from the storage system and the metadata from the CosmosDB database.
    //             //Failure to find the file to be deleted will be logged, but not considered a failure state.
    //             //I don't know what would cause "Terminal Failure" to show, but I know it would indeed be terminal, so that's what the default value gets to be.
    //             string deletionStatus = "Terminal Failure";
    //             if (await _cosmosDbWrapper.GetItemAsync<UserMetadata>(m.id, m.userid) != null)
    //             {
    //                 await _cosmosDbWrapper.DeleteItemAsync(m.id, m.userid);
    //                 deletionStatus = "File Found And Deleted";
    //             }
    //             else
    //             {
    //                 deletionStatus = "File Not Found";
                    
    //             }
    //             log.SetAttribute("deletion.status", deletionStatus);

    //             var blobStorage = new BlobStorageWrapper(_configuration);
    //             await blobStorage.DeleteBlob(m.userid, m.filename);

    //             HttpResponse response = context.Response;
    //             response.StatusCode = 200;
    //             response.ContentLength = Encoding.UTF8.GetByteCount(deletionStatus + ": " + m.filename);
    //             response.ContentType = "text/plain; charset=utf-8";

    //             await using (var bodyWriter = new StreamWriter(response.Body, leaveOpen: true))
    //             {
    //                 await bodyWriter.WriteAsync(deletionStatus + ": " + m.filename);
    //                 await bodyWriter.FlushAsync();
    //             }
    //         }
    //         catch(Exception e)
    //         {
    //             log.HandleException(e);
    //         }
    //     }
    // }
}