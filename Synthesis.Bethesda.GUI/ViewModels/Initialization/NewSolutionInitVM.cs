﻿using Mutagen.Bethesda;
using Noggog;
using Noggog.WPF;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Collections.Generic;
using System.Data.HashFunction.xxHash;
using System.IO;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Synthesis.Bethesda.GUI
{
    public class NewSolutionInitVM : ASolutionInitializer
    {
        public PathPickerVM ParentDirPath { get; } = new PathPickerVM();

        [Reactive]
        public string SolutionName { get; set; } = string.Empty;

        [Reactive]
        public string ProjectName { get; set; } = string.Empty;

        private readonly ObservableAsPropertyHelper<GetResponse<string>> _SolutionPath;
        public GetResponse<string> SolutionPath => _SolutionPath.Value;

        public override IObservable<GetResponse<Func<SolutionPatcherVM, Task>>> InitializationCall { get; }

        private readonly ObservableAsPropertyHelper<ErrorResponse> _ProjectError;
        public ErrorResponse ProjectError => _ProjectError.Value;

        private readonly ObservableAsPropertyHelper<string> _ProjectNameWatermark;
        public string ProjectNameWatermark => _ProjectNameWatermark.Value;

        public NewSolutionInitVM()
        {
            ParentDirPath.PathType = PathPickerVM.PathTypeOptions.Folder;
            ParentDirPath.ExistCheckOption = PathPickerVM.CheckOptions.On;

            _SolutionPath = Observable.CombineLatest(
                this.ParentDirPath.PathState(),
                this.WhenAnyValue(x => x.SolutionName),
                (parentDir, slnName) =>
                {
                    if (string.IsNullOrWhiteSpace(slnName)) return GetResponse<string>.Fail(val: slnName, reason: "Solution needs a name.");

                    // Will reevaluate once parent dir is fixed
                    if (parentDir.Failed) return GetResponse<string>.Succeed(value: slnName);
                    try
                    {
                        var slnPath = Path.Combine(parentDir.Value, slnName);
                        if (File.Exists(slnPath))
                        {
                            return GetResponse<string>.Fail(val: slnName, reason: $"Target solution folder cannot already exist as a file: {slnPath}");
                        }
                        if (Directory.Exists(slnPath)
                            && (Directory.EnumerateFiles(slnPath).Any()
                            || Directory.EnumerateDirectories(slnPath).Any()))
                        {
                            return GetResponse<string>.Fail(val: slnName, reason: $"Target solution folder must be empty: {slnPath}");
                        }
                        return GetResponse<string>.Succeed(Path.Combine(slnPath, $"{slnName}.sln"));
                    }
                    catch (ArgumentException)
                    {
                        return GetResponse<string>.Fail(val: slnName, reason: "Improper solution name. Go simpler.");
                    }
                })
                .ToGuiProperty(this, nameof(SolutionPath));

            var validation = Observable.CombineLatest(
                    this.ParentDirPath.PathState(),
                    this.WhenAnyValue(x => x.SolutionName),
                    this.WhenAnyValue(x => x.SolutionPath),
                    this.WhenAnyValue(x => x.ProjectName),
                    (parentDir, slnName, sln, proj) =>
                    {
                        // Use solution name if proj empty.
                        if (string.IsNullOrWhiteSpace(proj))
                        {
                            proj = SolutionNameProcessor(slnName);
                        }
                        return (parentDir, sln, proj, validation: ValidateProjectPath(proj, sln));
                    })
                .Replay(1)
                .RefCount();

            _ProjectError = validation
                .Select(i => (ErrorResponse)i.validation)
                .ToGuiProperty<ErrorResponse>(this, nameof(ProjectError), ErrorResponse.Success);

            InitializationCall = validation
                .Select((i) =>
                {
                    if (i.parentDir.Failed) return i.parentDir.BubbleFailure<Func<SolutionPatcherVM, Task>>();
                    if (i.sln.Failed) return i.sln.BubbleFailure<Func<SolutionPatcherVM, Task>>();
                    if (i.validation.Failed) return i.validation.BubbleFailure<Func<SolutionPatcherVM, Task>>();
                    return GetResponse<Func<SolutionPatcherVM, Task>>.Succeed(async (patcher) =>
                    {
                        CreateSolutionFile(i.sln.Value);
                        CreateProject(i.validation.Value, patcher.Profile.Release.ToCategory());
                        AddProjectToSolution(i.sln.Value, i.validation.Value);
                        patcher.SolutionPath.TargetPath = i.sln.Value;
                        var projName = Path.GetFileNameWithoutExtension(i.validation.Value);
                        // Little delay, just to make sure things populated properly.  Might not be needed
                        await Task.Delay(300);
                        patcher.ProjectSubpath = Path.Combine(projName, $"{projName}.csproj");
                    });
                });

            _ProjectNameWatermark = this.WhenAnyValue(x => x.SolutionName)
                .Select(x => string.IsNullOrWhiteSpace(x) ? "The name of the patcher" : SolutionNameProcessor(x))
                .ToGuiProperty<string>(this, nameof(ProjectNameWatermark));
        }

        private string SolutionNameProcessor(string slnName) => slnName.Replace(" ", string.Empty);
    }
}