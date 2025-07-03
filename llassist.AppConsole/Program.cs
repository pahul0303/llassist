﻿using System.Text.Json;
using Microsoft.Extensions.Logging;

using llassist.ApiService.Services;
using llassist.Common.Models;
using llassist.Common.Models.V1;
using llassist.Common.Mappers;

namespace llassist.AppConsole;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length != 2) // Adjusted to expect 2 arguments
        {
            Console.WriteLine("Usage: dotnet run <input_csv_file> <research_questions_file>");
            return;
        }

        string inputFile = args[0];
        string researchQuestionsFile = args[1]; // The second argument is the path to the research questions file

        // Read research questions from the file
        var researchQuestionsJson = await File.ReadAllTextAsync(researchQuestionsFile);
        var researchQuestions = JsonSerializer.Deserialize<ResearchQuestions>(researchQuestionsJson);

        if (researchQuestions == null)
        {
            Console.WriteLine("Failed to deserialize research questions.");
            return;
        }

        // Output the parsed research questions
        Console.WriteLine("Parsed Research Questions:");
        foreach (var definition in researchQuestions.Definitions)
        {
            Console.WriteLine($"Definition: {definition}");
        }

        foreach (var question in researchQuestions.Questions)
        {
            Console.WriteLine($"Question: {question.Text}");
            foreach (var definition in question.Definitions)
            {
                Console.WriteLine($"\tDefinition: {definition}");
            }
        }

        string baseOutputFile = Path.GetFileNameWithoutExtension(inputFile);
        string jsonOutputFile = $"{baseOutputFile}-result.json";
        string csvOutputFile = $"{baseOutputFile}-result.csv";

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
        });

        ILogger<NLPService> logger = loggerFactory.CreateLogger<NLPService>();
        var llmService = new LLMService("notrequired");

        var nlpService = new NLPService(llmService, logger);

        // Checkpoint
        var projectId = Ulid.NewUlid();
        var fileContents = await File.ReadAllTextAsync(inputFile);
        var articles = ArticleService.ReadArticlesFromCsv(new StringReader(fileContents), projectId);

        var csvWriter = ArticleService.BeginCsvWriting(csvOutputFile, researchQuestions.Questions.Select(q => q.Text).ToArray());

        for (int i = 0; i < articles.Count; i++)
        {
            var article = articles[i];
            double progressPercentage = (i / (double)articles.Count) * 100;
            Console.WriteLine($"Processing article {i + 1} out of {articles.Count} ({progressPercentage:F2}%) ...");
            Console.WriteLine($"Article: {article.Title}");

            var keySemantics = await nlpService.ExtractKeySemantics($"Title: {article.Title}\n Abstract: {article.Abstract}");
            article.ArticleKeySemantics = ModelMappers.ToArticleKeySemantics(article.Id, keySemantics);
            Console.WriteLine($"Topics: {string.Join(", ", keySemantics.Topics)}");
            Console.WriteLine($"Entities: {string.Join(", ", keySemantics.Entities)}");
            Console.WriteLine($"Keywords: {string.Join(", ", keySemantics.Keywords)}");

            bool mustRead = false;
            var relevanceList = new List<Relevance>();
            for (int j = 0; j < researchQuestions.Questions.Count; j++)
            {
                var researchQuestion = researchQuestions.Questions[j];
                var combinedDefinitions = researchQuestions.Definitions.Concat(researchQuestion.Definitions).ToArray();
                var relevance = await nlpService.EstimateRevelance(
                    $"Title: {article.Title}\n Abstract: {article.Abstract} \n Metadata: {JsonSerializer.Serialize(keySemantics)}", "abstract",
                    researchQuestion.Text, combinedDefinitions);
                relevanceList.Add(relevance);
                Console.WriteLine($"RQ-{j + 1} -- IR:{relevance.IsRelevant} RS:{relevance.RelevanceScore} IC:{relevance.IsContributing} CS:{relevance.ContributionScore}");
                Console.WriteLine($"\t>> RR: {relevance.RelevanceReason.Substring(0, Math.Min(120, relevance.RelevanceReason.Length))}...");
                Console.WriteLine($"\t>> CR: {relevance.ContributionReason.Substring(0, Math.Min(120, relevance.ContributionReason.Length))}...");
                mustRead = mustRead || relevance.IsRelevant || relevance.IsContributing;
            }

            var jobId = Ulid.NewUlid();
            article.ArticleRelevances = ModelMappers.ToArticleRelevances(article.Id, jobId, relevanceList);
            article.MustRead = mustRead;
            Console.WriteLine($"Decision: {(mustRead ? "Must Read" : "Skip")}\n");

            // Incremental CSV writing
            ArticleService.WriteArticleToCsv(csvWriter, article);
        }

        // Finalize CSV writing
        ArticleService.EndCsvWriting(csvWriter);

        // JSON output
        ArticleService.WriteArticlesToJson(articles, jsonOutputFile);

        Console.WriteLine($"Successfully extracted {articles.Count} articles to {jsonOutputFile} and {csvOutputFile}");
        Console.WriteLine($"Processed {researchQuestions.Questions.Count} research questions.");
    }
}
