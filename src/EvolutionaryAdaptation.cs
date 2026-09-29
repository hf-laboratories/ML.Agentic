namespace HFLabs.ML.Agentic;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Represents a strategy candidate in an evolutionary population with evaluated fitness and metrics.
/// </summary>
/// <typeparam name="T">Strategy or prompt phenotype type.</typeparam>
/// <param name="Id">Unique identifier of candidate.</param>
/// <param name="Strategy">The strategy phenotype.</param>
/// <param name="Fitness">Empirical fitness score (higher is better).</param>
/// <param name="Generation">Generation index where candidate was minted.</param>
/// <param name="Metrics">Individual objective metrics.</param>
public sealed record StrategyCandidate<T>(
    string Id,
    T Strategy,
    double Fitness,
    int Generation,
    IReadOnlyDictionary<string, double> Metrics);

/// <summary>
/// The state and best individual of an evolutionary generation.
/// </summary>
/// <typeparam name="T">Strategy phenotype type.</typeparam>
/// <param name="GenerationIndex">Generation index (0 for seed population).</param>
/// <param name="Candidates">Ranked candidates in the population.</param>
/// <param name="BestCandidate">The top-performing individual in this generation.</param>
public sealed record EvolutionGeneration<T>(
    int GenerationIndex,
    IReadOnlyList<StrategyCandidate<T>> Candidates,
    StrategyCandidate<T> BestCandidate);

/// <summary>
/// Contract for fitness evaluation, mutation, and crossover of strategy candidates conforming to PAT-CS-257.
/// </summary>
/// <typeparam name="T">Strategy phenotype type.</typeparam>
public interface IEvolutionEngine<T>
{
    /// <summary>
    /// Evaluates the empirical fitness score of a candidate strategy.
    /// </summary>
    /// <param name="strategy">Candidate strategy to evaluate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Fitness score.</returns>
    Task<double> EvaluateFitnessAsync(T strategy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a stochastic mutation operator to produce an altered strategy variant.
    /// </summary>
    /// <param name="parent">Parent strategy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Mutated offspring.</returns>
    Task<T> MutateAsync(T parent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Combines traits from two parent strategies to produce a crossover offspring.
    /// </summary>
    /// <param name="parentA">First parent.</param>
    /// <param name="parentB">Second parent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Recombined offspring.</returns>
    Task<T> CrossoverAsync(T parentA, T parentB, CancellationToken cancellationToken = default);
}

/// <summary>
/// Evolutionary optimization harness that adapts prompt strategies, parameters, or heuristics over multiple generations conforming to PAT-CS-257.
/// </summary>
/// <typeparam name="T">Strategy phenotype type.</typeparam>
public sealed class EvolutionaryStrategyOptimizer<T>(
    IEvolutionEngine<T> engine,
    int populationSize = 10,
    int generations = 5,
    double elitismRatio = 0.2)
{
    private readonly IEvolutionEngine<T> _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    private readonly int _populationSize = Math.Max(2, populationSize);
    private readonly int _generations = Math.Max(1, generations);
    private readonly double _elitismRatio = Math.Clamp(elitismRatio, 0.0, 1.0);

    /// <summary>
    /// Evolves the candidate strategy population across multiple generations.
    /// </summary>
    /// <param name="initialSeeds">Seed strategies.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Final generation state containing the elite candidate.</returns>
    public async Task<EvolutionGeneration<T>> EvolveAsync(
        IReadOnlyList<T> initialSeeds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initialSeeds);
        if (initialSeeds.Count == 0)
        {
            throw new ArgumentException("At least one initial seed is required.", nameof(initialSeeds));
        }

        List<StrategyCandidate<T>> population = [];

        // Evaluate seed population
        for (int i = 0; i < initialSeeds.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double fitness = await _engine.EvaluateFitnessAsync(initialSeeds[i], cancellationToken).ConfigureAwait(false);
            population.Add(new StrategyCandidate<T>(
                $"seed-{i}",
                initialSeeds[i],
                fitness,
                0,
                new Dictionary<string, double> { ["fitness"] = fitness }));
        }

        // Pad population to target size if seeds are fewer
        while (population.Count < _populationSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parent = population[Random.Shared.Next(initialSeeds.Count)];
            var mutated = await _engine.MutateAsync(parent.Strategy, cancellationToken).ConfigureAwait(false);
            double fitness = await _engine.EvaluateFitnessAsync(mutated, cancellationToken).ConfigureAwait(false);
            population.Add(new StrategyCandidate<T>(
                $"seed-mutated-{population.Count}",
                mutated,
                fitness,
                0,
                new Dictionary<string, double> { ["fitness"] = fitness }));
        }

        population = population.OrderByDescending(c => c.Fitness).ToList();
        int eliteCount = Math.Max(1, (int)(_populationSize * _elitismRatio));

        for (int gen = 1; gen <= _generations; gen++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nextGeneration = population.Take(eliteCount).Select(e => e with { Generation = gen }).ToList();

            while (nextGeneration.Count < _populationSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parentA = population[Random.Shared.Next(eliteCount)];
                var parentB = population[Random.Shared.Next(eliteCount)];

                T offspring;
                if (Random.Shared.NextDouble() < 0.5)
                {
                    offspring = await _engine.CrossoverAsync(parentA.Strategy, parentB.Strategy, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    offspring = await _engine.MutateAsync(parentA.Strategy, cancellationToken).ConfigureAwait(false);
                }

                double fitness = await _engine.EvaluateFitnessAsync(offspring, cancellationToken).ConfigureAwait(false);
                nextGeneration.Add(new StrategyCandidate<T>(
                    $"gen{gen}-{nextGeneration.Count}",
                    offspring,
                    fitness,
                    gen,
                    new Dictionary<string, double> { ["fitness"] = fitness }));
            }

            population = nextGeneration.OrderByDescending(c => c.Fitness).ToList();
        }

        return new EvolutionGeneration<T>(_generations, population, population[0]);
    }
}
