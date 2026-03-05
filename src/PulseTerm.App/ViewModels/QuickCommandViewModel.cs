using System;
using PulseTerm.Core.Models;
using ReactiveUI;

namespace PulseTerm.App.ViewModels;

public class QuickCommandViewModel : ReactiveObject
{
    private readonly QuickCommand _model;

    private string _name;
    private string _category;
    private string _commandText;
    private string _description;

    public QuickCommandViewModel(QuickCommand model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _name = model.Name;
        _category = model.Category;
        _commandText = model.CommandText;
        _description = model.Description;
    }

    public Guid Id => _model.Id;

    public bool IsBuiltIn => _model.IsBuiltIn;

    public string Name
    {
        get => _name;
        set
        {
            if (!IsBuiltIn)
            {
                this.RaiseAndSetIfChanged(ref _name, value);
                _model.Name = value;
            }
        }
    }

    public string Category
    {
        get => _category;
        set
        {
            if (!IsBuiltIn)
            {
                this.RaiseAndSetIfChanged(ref _category, value);
                _model.Category = value;
            }
        }
    }

    public string CommandText
    {
        get => _commandText;
        set
        {
            if (!IsBuiltIn)
            {
                this.RaiseAndSetIfChanged(ref _commandText, value);
                _model.CommandText = value;
            }
        }
    }

    public string Description
    {
        get => _description;
        set
        {
            if (!IsBuiltIn)
            {
                this.RaiseAndSetIfChanged(ref _description, value);
                _model.Description = value;
            }
        }
    }

    public QuickCommand ToModel() => _model;
}
