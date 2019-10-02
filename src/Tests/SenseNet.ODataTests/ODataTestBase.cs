﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SenseNet.Configuration;
using SenseNet.ContentRepository;
using SenseNet.ContentRepository.Schema;
using SenseNet.ContentRepository.Security;
using SenseNet.ContentRepository.Storage;
using SenseNet.ContentRepository.Storage.Data;
using SenseNet.ContentRepository.Storage.DataModel;
using SenseNet.ContentRepository.Storage.Security;
using SenseNet.ContentRepository.Volatile;
using SenseNet.ContentRepository.Workspaces;
using SenseNet.OData;
using SenseNet.ODataTests.Responses;
using SenseNet.Search;
using SenseNet.Security;
using SenseNet.Security.Data;
using SenseNet.Tests;
using SenseNet.Tests.Accessors;
using Task = System.Threading.Tasks.Task;
// ReSharper disable StringLiteralTypo

namespace SenseNet.ODataTests
{
    public class ODataResponse
    {
        public int StatusCode { get; set; }
        public string Result { get; set; }
    }
    public class ODataTestBase
    {
        #region Infrastructure

        private static RepositoryInstance _repository;

        protected static RepositoryBuilder CreateRepositoryBuilder()
        {
            var dataProvider = new InMemoryDataProvider();
            Providers.Instance.DataProvider = dataProvider;

            return new RepositoryBuilder()
                .UseAccessProvider(new DesktopAccessProvider())
                .UseDataProvider(dataProvider)
                .UseSharedLockDataProviderExtension(new InMemorySharedLockDataProvider())
                .UseBlobMetaDataProvider(new InMemoryBlobStorageMetaDataProvider(dataProvider))
                .UseBlobProviderSelector(new InMemoryBlobProviderSelector())
                .UseAccessTokenDataProviderExtension(new InMemoryAccessTokenDataProvider())
                .UseSearchEngine(new InMemorySearchEngine(GetInitialIndex()))
                .UseSecurityDataProvider(GetSecurityDataProvider(dataProvider))
                .UseElevatedModificationVisibilityRuleProvider(new ElevatedModificationVisibilityRule())
                .StartWorkflowEngine(false)
                //.DisableNodeObservers()
                //.EnableNodeObservers(typeof(SettingsCache))
                .UseTraceCategories("Test", "Event", "Custom") as RepositoryBuilder;
        }
        protected static ISecurityDataProvider GetSecurityDataProvider(InMemoryDataProvider repo)
        {
            return new MemoryDataProvider(new DatabaseStorage
            {
                Aces = new List<StoredAce>
                {
                    new StoredAce {EntityId = 2, IdentityId = 1, LocalOnly = false, AllowBits = 0x0EF, DenyBits = 0x000}
                },
                Entities = repo.LoadEntityTreeAsync(CancellationToken.None).GetAwaiter().GetResult()
                    .ToDictionary(x => x.Id, x => new StoredSecurityEntity
                    {
                        Id = x.Id,
                        OwnerId = x.OwnerId,
                        ParentId = x.ParentId,
                        IsInherited = true,
                        HasExplicitEntry = x.Id == 2
                    }),
                Memberships = new List<Membership>
                {
                    new Membership
                    {
                        GroupId = Identifiers.AdministratorsGroupId,
                        MemberId = Identifiers.AdministratorUserId,
                        IsUser = true
                    }
                },
                Messages = new List<Tuple<int, DateTime, byte[]>>()
            });
        }

        private static InitialData _initialData;
        protected static InitialData GetInitialData()
        {
            return _initialData ?? (_initialData = InitialData.Load(InitialTestData.Instance));
        }

        private static InMemoryIndex _initialIndex;
        protected static InMemoryIndex GetInitialIndex()
        {
            //UNDONE:ODATA: TEST:BUG: Commented out lines maybe wrong
            //if (_initialIndex == null)
            //{
            //    var index = new InMemoryIndex();
            //    index.Load(new StringReader(InitialTestIndex.Index));
            //    _initialIndex = index;
            //}
            //return _initialIndex.Clone();
            var index = new InMemoryIndex();
            index.Load(new StringReader(InitialTestIndex.Index));
            _initialIndex = index;
            return _initialIndex;
        }

        [ClassCleanup]
        public void CleanupClass()
        {
            _repository?.Dispose();
        }
        #endregion

        protected void ODataTest(Action callback)
        {
            ODataTestAsync(() =>
            {
                callback();
                return Task.CompletedTask;
            }, true).ConfigureAwait(false).GetAwaiter().GetResult();
        }
        protected Task ODataTestAsync(Func<Task> callback)
        {
            return ODataTestAsync(callback, true);
        }

        protected Task IsolatedODataTestAsync(Func<Task> callback)
        {
            return ODataTestAsync(callback, false);
        }

        private async Task ODataTestAsync(Func<Task> callback, bool reused)
        {
            Cache.Reset();

            if (!reused || _repository == null)
            {
                _repository?.Dispose();
                _repository = null;

                var repoBuilder = CreateRepositoryBuilder();
                await DataStore.InstallInitialDataAsync(GetInitialData(), CancellationToken.None).ConfigureAwait(false);
                Indexing.IsOuterSearchEngineEnabled = true;
                _repository = Repository.Start(repoBuilder);
            }

            using (new SystemAccount())
                await callback().ConfigureAwait(false);

            if (!reused)
            {
                _repository?.Dispose();
                _repository = null;
            }
        }

        internal static Task<ODataResponse> ODataGetAsync(string resource, string queryString)
        {
            return ODataProcessRequestAsync(resource, queryString, null, "GET");
        }
        internal static Task<ODataResponse> ODataPutAsync(string resource, string queryString, string requestBodyJson)
        {
            return ODataProcessRequestAsync(resource, queryString, requestBodyJson, "PUT");
        }
        internal static Task<ODataResponse> ODataPatchAsync(string resource, string queryString, string requestBodyJson)
        {
            return ODataProcessRequestAsync(resource, queryString, requestBodyJson, "PATCH");
        }
        private static async Task<ODataResponse> ODataProcessRequestAsync(string resource, string queryString,
            string requestBodyJson, string httpMethod)
        {
            var httpContext = CreateHttpContext(resource, queryString);
            var request = httpContext.Request;
            request.Method = httpMethod;
            request.Path = resource;
            request.QueryString = new QueryString(queryString);
            if(requestBodyJson != null)
                request.Body = CreateRequestStream(requestBodyJson);

            httpContext.Response.Body = new MemoryStream();

            var odata = new ODataMiddleware(null);
            var odataRequest = ODataRequest.Parse(httpContext);
            await odata.ProcessRequestAsync(httpContext, odataRequest).ConfigureAwait(false);

            var responseOutput = httpContext.Response.Body;
            responseOutput.Seek(0, SeekOrigin.Begin);
            string output;
            using (var reader = new StreamReader(responseOutput))
                output = await reader.ReadToEndAsync().ConfigureAwait(false);

            return new ODataResponse { Result = output, StatusCode = httpContext.Response.StatusCode };
        }

        internal static HttpContext CreateHttpContext(string resource, string queryString)
        {
            var httpContext = new DefaultHttpContext();
            var request = httpContext.Request;
            request.Method = "GET";
            request.Path = resource;
            request.QueryString = new QueryString(queryString);
            httpContext.Response.Body = new MemoryStream();
            return httpContext;
        }

        /* ========================================================================= TOOLS */

        protected static ContentQuery CreateSafeContentQuery(string qtext)
        {
            var cquery = ContentQuery.CreateQuery(qtext, QuerySettings.AdminSettings);
            var cqueryAcc = new ObjectAccessor(cquery);
            cqueryAcc.SetFieldOrProperty("IsSafe", true);
            return cquery;
        }

        protected static readonly string CarContentType = @"<?xml version='1.0' encoding='utf-8'?>
<ContentType name='Car' parentType='ListItem' handler='SenseNet.ContentRepository.GenericContent' xmlns='http://schemas.sensenet.com/SenseNet/ContentRepository/ContentTypeDefinition'>
  <DisplayName>Car,DisplayName</DisplayName>
  <Description>Car,Description</Description>
  <Icon>Car</Icon>
  <AllowIncrementalNaming>true</AllowIncrementalNaming>
  <Fields>
    <Field name='Name' type='ShortText'/>
    <Field name='Make' type='ShortText'/>
    <Field name='Model' type='ShortText'/>
    <Field name='Style' type='Choice'>
      <Configuration>
        <AllowMultiple>false</AllowMultiple>
        <AllowExtraValue>true</AllowExtraValue>
        <Options>
          <Option value='Sedan' selected='true'>Sedan</Option>
          <Option value='Coupe'>Coupe</Option>
          <Option value='Cabrio'>Cabrio</Option>
          <Option value='Roadster'>Roadster</Option>
          <Option value='SUV'>SUV</Option>
          <Option value='Van'>Van</Option>
        </Options>
      </Configuration>
    </Field>
    <Field name='StartingDate' type='DateTime'/>
    <Field name='Color' type='Color'>
      <Configuration>
        <DefaultValue>#ff0000</DefaultValue>
        <Palette>#ff0000;#f0d0c9;#e2a293;#d4735e;#65281a</Palette>
      </Configuration>
    </Field>
    <Field name='EngineSize' type='ShortText'/>
    <Field name='Power' type='ShortText'/>
    <Field name='Price' type='Number'/>
    <Field name='Description' type='LongText'/>
  </Fields>
</ContentType>
";
        protected static void InstallCarContentType()
        {
            ContentTypeInstaller.InstallContentType(CarContentType);
        }

        protected static void EnsureManagerOfAdmin()
        {
            var content = Content.Create(User.Administrator);
            if (((IEnumerable<Node>)content["Manager"]).Count() > 0)
                return;
            content["Manager"] = User.Administrator;
            content["Email"] = "anybody@somewhere.com";
            content.Save();
        }


        protected static Workspace CreateWorkspace(string name = null)
        {
            var workspaces = Node.LoadNode("/Root/Workspaces");
            if (workspaces == null)
            {
                workspaces = new Folder(Repository.Root) { Name = "Workspaces" };
                workspaces.Save();
            }

            var workspace = new Workspace(workspaces) { Name = name ?? Guid.NewGuid().ToString() };
            workspace.Save();

            return workspace;
        }
        protected static SystemFolder CreateTestRoot(string name = null)
        {
            return CreateTestRoot(null, name);
        }
        protected static SystemFolder CreateTestRoot(Node parent, string name = null)
        {
            var systemFolder = new SystemFolder(parent ?? Repository.Root) { Name = name ?? Guid.NewGuid().ToString() };
            systemFolder.Save();
            return systemFolder;
        }


        protected static ODataEntityResponse GetEntity(ODataResponse response)
        {
            var text = response.Result;
            var result = new Dictionary<string, object>();
            var jo = (JObject)Deserialize(text);
            return ODataEntityResponse.Create((JObject)jo["d"]);
        }
        protected static ODataEntitiesResponse GetEntities(ODataResponse response)
        {
            var text = response.Result;

            var result = new List<ODataEntityResponse>();
            var jo = (JObject)Deserialize(text);
            var d = (JObject)jo["d"];
            var count = d["__count"].Value<int>();
            var jarray = (JArray)d["results"];
            for (int i = 0; i < jarray.Count; i++)
                result.Add(ODataEntityResponse.Create((JObject)jarray[i]));
            return new ODataEntitiesResponse(result.ToList(), count);
        }

        protected static ODataErrorResponse GetError(ODataResponse response, bool throwOnError = true)
        {
            var text = response.Result;
            if (text == null)
                throw new ArgumentNullException(nameof(text));

            var json = Deserialize(text);
            if (json == null)
            {
                if (throwOnError)
                    throw new InvalidOperationException("Deserialized text is null.");
                return null;
            }

            if (!(json["error"] is JObject error))
            {
                if (throwOnError)
                    throw new Exception("Object is not an error");
                return null;
            }

            var code = error["code"]?.Value<string>() ?? string.Empty;
            var exceptionType = error["exceptiontype"]?.Value<string>() ?? string.Empty;
            var message = error["message"] as JObject;
            var value = message?["value"]?.Value<string>() ?? string.Empty;
            var innerError = error["innererror"] as JObject;
            var trace = innerError?["trace"]?.Value<string>() ?? string.Empty;
            Enum.TryParse<ODataExceptionCode>(code, out var oeCode);
            return new ODataErrorResponse { Code = oeCode, ExceptionType = exceptionType, Message = value, StackTrace = trace };
        }
        protected void AssertNoError(ODataResponse response)
        {
            var error = GetError(response, false);
            if (error != null)
                Assert.Fail(error.Message);
        }

        protected static JContainer Deserialize(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            JContainer json;
            using (var reader = new StringReader(text))
                json = Deserialize(reader);
            return json;
        }
        protected static JContainer Deserialize(TextReader reader)
        {
            var models = reader?.ReadToEnd() ?? string.Empty;
            var settings = new JsonSerializerSettings { DateFormatHandling = DateFormatHandling.IsoDateFormat };
            var serializer = JsonSerializer.Create(settings);
            if (serializer == null)
                throw new InvalidOperationException("Serializer could not be created from settings.");

            var jreader = new JsonTextReader(new StringReader(models));
            var x = (JContainer)serializer.Deserialize(jreader);
            return x;
        }

        private static Stream CreateRequestStream(string request)
        {
            var stream = new MemoryStream();
            var writer = new StreamWriter(stream);
            writer.Write(request);
            writer.Flush();
            stream.Seek(0, SeekOrigin.Begin);
            return stream;
        }
    }
}