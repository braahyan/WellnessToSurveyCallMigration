using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using Dapper;
using Newtonsoft.Json;

namespace WellnessToSurveyCallMigration
{
    class Program
    {


        static void Main(string[] args)
        {
            var sql = "select * from FollowUpSurveyConfiguration";

            using var connection =
                new SqlConnection(
                    "server=.\\SQLExpress;database=VoiceFriend;Trusted_Connection=True;"
                );
            connection.Open();
            var configurations = connection.Query<FollowUpSurveyConfigurationDatabase>(sql)
                .Select(x => new FollowUpSurveyConfiguration()
                {
                    AccountId = x.AccountId,
                    InboundNumber = x.InboundNumber,
                    ConfigurationData =
                        JsonConvert.DeserializeObject<FollowUpSurveyConfigurationData>(x.ConfigurationData)
                }).ToList();
            Directory.CreateDirectory("results");
            foreach (var config in configurations)
            {

                var configData = config.ConfigurationData;
                var accountId = config.AccountId;
                if (configData != null)
                {
                    try
                    {
                        configData.DefaultSurveyQuestionListIds ??= new Dictionary<SurveyCallType, int>();
                        var wellnessCheckToQuestionIdMapping = new Dictionary<string, int>();

                        var wellnessCheckIds = GetWellnessCheckIdsFromSurveyCalls(connection, accountId);
                        
                        File.WriteAllText($"results/{accountId}.bak.json", JsonConvert.SerializeObject(configData, Formatting.Indented));
                        File.WriteAllText($"results/{accountId}.bak.wellnesschecks.json", JsonConvert.SerializeObject(wellnessCheckIds, Formatting.Indented));

                        foreach (var wellnessCheckId in wellnessCheckIds)
                        {
                            var wellnessCheck = WellnessCheckModel(connection, wellnessCheckId);
                            var questionListId = CreateQuestionSurveyListAndUpdateSurveyCall(connection, wellnessCheck, accountId,
                                        wellnessCheckToQuestionIdMapping);
                        }


                        foreach (var (surveyCallType, wellnessCheckId) in configData.DefaultSurveyQuestions)
                        {
                            var wellnessCheck = WellnessCheckModel(connection, wellnessCheckId);
                            if (wellnessCheck != null)
                            {
                                var questionListId = CreateQuestionSurveyListAndUpdateSurveyCall(connection, wellnessCheck, accountId,
                                    wellnessCheckToQuestionIdMapping);
                                configData.DefaultSurveyQuestionListIds[surveyCallType] = questionListId;
                            }
                            else
                            {
                                Console.WriteLine(
                                    $"Could not find wellnesscheck id {wellnessCheckId} from account id {accountId}");
                            }
                        }

                        // get wellness check data
                        foreach (var surveyCallTemplate in configData.SurveyCallData)
                        {
                            if (!configData.DefaultSurveyQuestions.ContainsValue(surveyCallTemplate.WellnessCheckId))
                            {
                                var wellnessCheck = WellnessCheckModel(connection, surveyCallTemplate.WellnessCheckId);
                                if (wellnessCheck != null)
                                {
                                    // create surveyquestionlist
                                    var questionListId = CreateQuestionSurveyListAndUpdateSurveyCall(connection, wellnessCheck, accountId,
                                        wellnessCheckToQuestionIdMapping);
                                    // set surveyquestionList field above
                                    surveyCallTemplate.SurveyQuestionListId = questionListId;
                                }
                                else
                                {
                                    Console.WriteLine(
                                        $"Could not find wellnesscheck id {surveyCallTemplate.WellnessCheckId} from account id {accountId}");
                                }
                            }
                            else
                            {
                                surveyCallTemplate.SurveyQuestionListId =
                                    configData.DefaultSurveyQuestionListIds[surveyCallTemplate.CallType];
                            }
                        }

                        UpdateFollowUpSurveyConfig(connection, accountId, config.InboundNumber,
                            JsonConvert.SerializeObject(configData));
                        File.WriteAllText($"results/{accountId}.mapping.json",
                            JsonConvert.SerializeObject(wellnessCheckToQuestionIdMapping));
                        File.WriteAllText($"results/{accountId}.json", JsonConvert.SerializeObject(configData));
                        Console.WriteLine("completed");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"Something went wrong with account #{accountId}: {e}");
                    }
                }
                else
                {
                    Console.WriteLine(
                        $"Could not find configuration data from account id {accountId}");
                }

            }
        }

        private static int CreateQuestionSurveyListAndUpdateSurveyCall(SqlConnection connection, WellnessCheckModel wellnessCheck,
            int accountId, Dictionary<string, int> mapping)
        {
            if (mapping.ContainsKey(wellnessCheck.Id))
            {
                return mapping[wellnessCheck.Id];
            }
            var questionListId = CreateSurveyQuestionList(connection, wellnessCheck.Name, "voice",
                accountId);

            mapping.Add(wellnessCheck.Id, questionListId);

            foreach (var content in wellnessCheck.Contents.Where(x=>MigratedTypes.Contains(x.Type)))
            {
                CreateSurveyQuestionContent(connection, questionListId, content.Ordinal,
                    content.Message, content.Type, "voice", accountId);
            }

            UpdateSurveyCall(connection, questionListId, accountId, wellnessCheck.Id);

            return questionListId;
        }

        private static WellnessCheckModel WellnessCheckModel(SqlConnection connection,
            string wellnessCheckId)
        {
            var wellnessCheck = connection.QuerySingleOrDefault<WellnessCheckModel>(
                "select Name, Id from wellnesscheck where Id = @id",
                new {id = wellnessCheckId});
            if (wellnessCheck != null)
            {
                wellnessCheck.Contents = connection.Query<WellnessCheckContentsModel>(
                    "select * from wellnesscheckcontent where WellnessCheckId = @id",
                    new {id = wellnessCheck.Id}).ToList();
            }

            return wellnessCheck;
        }

        public static IEnumerable<string> GetWellnessCheckIdsFromSurveyCalls(SqlConnection connection, int accountId)
        {
            var wellnessCheck = connection.Query<string>(
                "select distinct WellnessCheckId from surveycall where AccountId=@accountid;",
                new { accountId });
            return wellnessCheck;
        }

        public static void UpdateSurveyCall(SqlConnection connection, int surveyQuestionListId, int accountId,
            string wellnessCheckId)
        {
            connection.Execute(
                "update surveycall set SurveyQuestionLIstId = @SurveyQuestionListId where AccountId=@AccountId and WellnessCheckId=@WellnessCheckId",
                new
                {
                    SurveyQuestionListId=surveyQuestionListId,
                    AccountId=accountId,
                    WellnessCheckId=wellnessCheckId
                });
        }

        public static int CreateSurveyQuestionList(SqlConnection connection, string name, string type, int accountId)
        {
            return connection.ExecuteScalar<int>(
                "insert into SurveyQuestionList (Name, QuestionListType, AccountId) " +
                "OUTPUT Inserted.SurveyQuestionListId " +
                "values (@Name, @Type, @AccountId)",
                new
                {
                    Name = name,
                    Type = type,
                    AccountId = accountId
                });
        }

        public static int CreateSurveyQuestionContent(SqlConnection connection, int surveyQuestionListId, int ordinal,
            string content, string contentType, string responseType, int accountId)
        {
            return connection.Execute(
                @"INSERT INTO[dbo].[SurveyQuestionContent]
                ([SurveyQuestionListId]
                ,[Ordinal]
                ,[ContentId]
                ,[Content]
                ,[ContentType]
                ,[ResponseType]
                ,[AccountId])
                VALUES(
                @SurveyQuestionListId
                ,@Ordinal
                ,@ContentId
                ,@Content
                ,@ContentType
                ,@ResponseType
                ,@AccountId)
            ",
                new
                {
                    SurveyQuestionListId = surveyQuestionListId,
                    Ordinal = ordinal,
                    ContentId = 0,
                    Content = content,
                    ContentType = ConvertContentType(contentType),
                    ResponseType = responseType,
                    AccountId = accountId
                });
        }

        public static SurveyQuestionContentTypeEnum ConvertContentType(string contentType)
        {
            if (contentType == "Outbound")
            {
                return SurveyQuestionContentTypeEnum.Greeting;
            }

            if (contentType == "TelephoneSurvey")
            {
                return SurveyQuestionContentTypeEnum.Question;
            }

            if (contentType == "OutboundFarewell")
            {
                return SurveyQuestionContentTypeEnum.Farewell;
            }

            return Enum.Parse<SurveyQuestionContentTypeEnum>(contentType);
        }

        private static List<string> MigratedTypes = new List<string>
            {"Outbound", "TelephoneSurvey", "OutboundFarewell"};

        public static void UpdateFollowUpSurveyConfig(SqlConnection connection, int accountId, string number, string data)
        {
            var sql = @"UPDATE [dbo].[FollowUpSurveyConfiguration]
                           SET [ConfigurationData] = @ConfigurationData
                         WHERE AccountID=@AccountId and InboundNumber=@Number";
            connection.Execute(sql, new
            {
                ConfigurationData = data,
                AccountId = accountId,
                Number = number
            });
        }
    }

    

    class WellnessCheckModel
    {
        public string Name { get; set; }
        public string Id { get; set; }
        public List<WellnessCheckContentsModel> Contents { get; set; }
    }
    class WellnessCheckContentsModel
    {
        public string Name { get; set; }
        public string WellnessCheckId { get; set; }
        public string Message { get; set; }
        public int Ordinal { get; set; }
        public string Type { get; set; }
    }
    class FollowUpSurveyConfigurationDatabase
    {
        public int AccountId { get; set; }
        public string InboundNumber { get; set; }
        public string ConfigurationData { get; set; }
    }

    public class FollowUpSurveyConfiguration
    {
        public int AccountId { get; set; }
        public string InboundNumber { get; set; }
        public FollowUpSurveyConfigurationData ConfigurationData { get; set; }
    }

    public enum SurveyCallType
    {
        Manual,
        Auto
    }

    public enum SurveyCallTelephonyStatus
    {
        Incomplete,
        Complete,
        Cancelled
    }

    public enum SurveyCallTaskStatus
    {
        Incomplete,
        Complete
    }

    public enum SurveyCallTransferStatus
    {
        Transferred,
        Complete,
        Incomplete
    }

    public enum RedialStrategy
    {
        NoRedials,
        Nexion,
        NextDay,
        NexDayNoWeekends,
        FiveMinutes,
        CedarCommunity,
        TwoHourAndNextDay,
        MarquisBriarwoodRedialStrategy
    }

    public class SurveyCallTemplateModel
    {
        public SurveyCallType CallType { get; set; }
        public int OffsetDays { get; set; }
        public SurveyCallTelephonyStatus TelephonyStatus { get; set; }
        public SurveyCallTaskStatus TaskStatus { get; set; }
        public RedialStrategy RedialStrategy { get; set; }
        public string WellnessCheckId { get; set; }
        public int SurveyQuestionListId { get; set; }
    }

    public class FollowUpSurveyConfigurationData
    {
        public string ForwardNumber { get; set; }

        public Dictionary<SurveyCallType, string> DefaultSurveyQuestions { get; set; }
        public Dictionary<SurveyCallType, int> DefaultSurveyQuestionListIds { get; set; }
        public IEnumerable<SurveyCallTemplateModel> SurveyCallData { get; set; }
        public Dictionary<string, FacilityInfoMapping> FacilityInfoToCallerIdMapping { get; set; }
        public StatusDefinition StatusConfiguration { get; set; }
        public string[] EmailNotificationData { get; set; }
        public List<string> Tags { get; set; }
        public ScheduleStrategyEnum ScheduleStrategy { get; set; }
        public string SurveyCallResponsePattern { get; set; }
    }

    public class FacilityInfoMapping
    {
        public string CallerId { get; set; }
        public string TimeZone { get; set; }
    }


    public enum FollowUpSurveyOperation
    {
        Error,
        Enroll,
        End
    }

    public enum ScheduleStrategyEnum
    {
        None,
        NoWeekends
    }
    public enum SurveyQuestionContentTypeEnum
    {
        Greeting, Question, Farewell
    }
    public class StatusDefinition
    {

        public StatusDefinition()
        {
            this.Transitions = new Dictionary<string, FollowUpSurveyOperation>();
            this.States = new List<string>();
            this.Forbidden = new List<string>();
        }

        public List<string> States { get; set; }

        public Dictionary<string, FollowUpSurveyOperation> Transitions { get; set; }

        public List<string> Forbidden { get; set; }

        public string EndState { get; set; }

        public string StartState { get; set; }
        public bool MovePatientToEndStateOnLastCall { get; set; }

        public List<String> GetEnrollingStates()
        {
            var transitions = Transitions
                .Where(x => x.Value == FollowUpSurveyOperation.Enroll)
                .Select(x => x.Key);
            return new HashSet<string>(transitions.Select(x => x.Split(':')[1]).Where(x => x != "*")).ToList();
        }


        public FollowUpSurveyOperation? Transition(string previousState, string nextState)
        {
            var transitionKey = $"{previousState}:{nextState}";
            var wildCardTo = $"*:{nextState}";
            var wildCardFrom = $"{previousState}:*";

            if (previousState == nextState)
            {
                return null;
            }

            if (Forbidden.Contains(transitionKey) ||
                Forbidden.Contains(wildCardTo) ||
                Forbidden.Contains(wildCardFrom))
            {
                return FollowUpSurveyOperation.Error;
            }

            // look for specific key
            if (Transitions.ContainsKey(transitionKey))
            {
                return Transitions[transitionKey];
            }

            // look for any transition moving *to* this state
            if (Transitions.ContainsKey(wildCardTo))
            {
                return Transitions[wildCardTo];
            }

            // look for any transition moving *away* from previous
            if (Transitions.ContainsKey(wildCardFrom))
            {
                return Transitions[wildCardFrom];
            }

            return null;
        }
    }
}
