using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using System.Timers;

namespace Blazored.Typeahead
{
    public class BlazoredTypeaheadBase<TItem, TValue> : ComponentBase, IDisposable
    {
        [Inject] private IJSRuntime JSRuntime { get; set; }

        [CascadingParameter] private EditContext CascadedEditContext { get; set; }

        [Parameter] public TValue Value { get; set; }
        [Parameter] public EventCallback<TValue> ValueChanged { get; set; }
        [Parameter] public Expression<Func<TValue>> ValueExpression { get; set; }
        [Parameter] public Func<string, Task<IEnumerable<TItem>>> SearchMethod { get; set; }
        [Parameter] public Func<TItem, TValue> ConvertMethod { get; set; }

        [Parameter] public RenderFragment NotFoundTemplate { get; set; }
        [Parameter] public RenderFragment<TItem> ResultTemplate { get; set; }
        [Parameter] public RenderFragment<TValue> SelectedTemplate { get; set; }
        [Parameter] public RenderFragment FooterTemplate { get; set; }

        [Parameter(CaptureUnmatchedValues = true)] public IReadOnlyDictionary<string, object> AdditionalAttributes { get; set; }
        [Parameter] public int MinimumLength { get; set; } = 1;
        [Parameter] public int Debounce { get; set; } = 300;
        [Parameter] public int MaximumSuggestions { get; set; } = 25;
        [Parameter] public bool Disabled { get; set; } = false;

        protected bool IsSearching { get; private set; } = false;
        protected bool IsShowingSuggestions { get; private set; } = false;
        protected bool IsShowingSearchbar { get; private set; } = true;
        protected bool IsShowingMask { get; private set; } = false;
        protected TItem[] Suggestions { get; set; } = new TItem[0];
        protected int SelectedIndex { get; set; }
        protected string SearchText
        {
            get => _searchText;
            set
            {
                _searchText = value;

                if (value.Length == 0)
                {
                    _debounceTimer.Stop();
                    Suggestions = new TItem[0];
                    SelectedIndex = -1;
                }
                else if (value.Length >= MinimumLength)
                {
                    _debounceTimer.Stop();
                    _debounceTimer.Start();
                }
            }
        }

        protected ElementReference searchInput;
        protected ElementReference mask;
        protected ElementReference typeahead;

        private EditContext _editContext;
        private FieldIdentifier _fieldIdentifier;
        private Timer _debounceTimer;
        private string _searchText = string.Empty;

        protected override void OnInitialized()
        {
            if (SearchMethod == null)
            {
                throw new InvalidOperationException($"{GetType()} requires a {nameof(SearchMethod)} parameter.");
            }

            if (ConvertMethod == null)
            {
                if (typeof(TItem) != typeof(TValue))
                {
                    throw new InvalidOperationException($"{GetType()} requires a {nameof(ConvertMethod)} parameter.");
                }

                ConvertMethod = item => item is TValue value ? value : default;
            }

            if (SelectedTemplate == null)
            {
                throw new InvalidOperationException($"{GetType()} requires a {nameof(SelectedTemplate)} parameter.");
            }

            if (ResultTemplate == null)
            {
                throw new InvalidOperationException($"{GetType()} requires a {nameof(ResultTemplate)} parameter.");
            }

            _debounceTimer = new Timer();
            _debounceTimer.Interval = Debounce;
            _debounceTimer.AutoReset = false;
            _debounceTimer.Elapsed += Search;

            _editContext = CascadedEditContext;
            _fieldIdentifier = FieldIdentifier.Create(ValueExpression);

            Initialize();
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                await Interop.AddKeyDownEventListener(JSRuntime, searchInput);
                await Interop.AddFocusOutEventListener(JSRuntime, typeahead);
               // Interop.OnEscapeEvent += OnEscape;
                Interop.OnFocusOutEvent += OnFocusOut;
            }
        }

        protected override void OnParametersSet()
        {
            Initialize();
        }

        private void Initialize()
        {
            SearchText = "";
            IsShowingSuggestions = false;
            if (Value == null)
            {
                IsShowingMask = false;
                IsShowingSearchbar = true;
            }
            else
            {
                IsShowingSearchbar = false;
                IsShowingMask = true;
            }
        }

        protected async Task HandleClear()
        {
            SearchText = "";
            await ValueChanged.InvokeAsync(default);
            _editContext?.NotifyFieldChanged(_fieldIdentifier);

            await Task.Delay(250); // Possible race condition here.
            await Interop.Focus(JSRuntime, searchInput);
        }

        protected async Task HandleClickOnMask()
        {
            IsShowingMask = false;
            IsShowingSearchbar = true;

            await Task.Delay(250); // Possible race condition here.
            await Interop.Focus(JSRuntime, searchInput);
        }

        protected async Task ShowMaximumSuggestions()
        {
            IsShowingSuggestions = !IsShowingSuggestions;

            if (IsShowingSuggestions)
            {
                SearchText = "";
                IsSearching = true;
                await InvokeAsync(StateHasChanged);

                Suggestions = (await SearchMethod?.Invoke(_searchText)).Take(MaximumSuggestions).ToArray();

                IsSearching = false;
                await InvokeAsync(StateHasChanged);
            }
        }

        protected async Task HandleKeyUpOnShowMaximum(KeyboardEventArgs args)
        {
            if (args.Key == "Enter")
                await ShowMaximumSuggestions();
        }

        protected async Task HandleKeyUpOnMask(KeyboardEventArgs args)
        {
            switch (args.Key)
            {
                case "Enter":
                    IsShowingMask = false;
                    IsShowingSearchbar = true;
                    await Task.Delay(250); // Possible race condition here.
                    await Interop.Focus(JSRuntime, searchInput);
                    break;
                case "Backspace":
                case "Delete":
                    await HandleClear();
                    break;
                default:
                    break;
            }
        }

        public async Task HandleKeyup(KeyboardEventArgs args)
        {
            if (args.Key == "ArrowDown")
                MoveSelection(1);
            else if (args.Key == "ArrowUp")
                MoveSelection(-1);
            else if (args.Key == "Enter" && SelectedIndex >= 0 && SelectedIndex < Suggestions.Count())
                await SelectResult(Suggestions[SelectedIndex]);
        }

        private void MoveSelection(int count)
        {
            var index = SelectedIndex + count;

            if (index >= Suggestions.Count())
                index = 0;

            if (index < 0)
                index = Suggestions.Count() - 1;

            SelectedIndex = index;
        }

        protected string GetSelectedSuggestionClass(TItem item, int index)
        {
            const string resultClass = "blazored-typeahead__result-selected";
            TValue value = ConvertMethod(item);

            if (index == SelectedIndex)
                return resultClass;

            return Equals(value, Value) ? resultClass : string.Empty;
        }

        protected async void Search(Object source, ElapsedEventArgs e)
        {
            IsSearching = true;
            await InvokeAsync(StateHasChanged);
            Suggestions = (await SearchMethod?.Invoke(_searchText)).Take(MaximumSuggestions).ToArray();

            IsSearching = false;
            IsShowingSuggestions = true;
            await InvokeAsync(StateHasChanged);
        }

        protected async Task SelectResult(TItem item)
        {
            TValue value = ConvertMethod(item);

            await ValueChanged.InvokeAsync(value);
            _editContext?.NotifyFieldChanged(_fieldIdentifier);
        }

        protected bool ShouldShowSuggestions()
        {
            return IsShowingSuggestions &&
                   Suggestions.Any();
        }

        private bool IsSearchingOrDebouncing => IsSearching || _debounceTimer.Enabled;

        protected bool ShowNotFound()
        {
            return IsShowingSuggestions &&
                   !IsSearchingOrDebouncing &&
                   !Suggestions.Any();
        }

        protected void OnFocusOut(object sender, EventArgs e)
        {
            Initialize();
            InvokeAsync(StateHasChanged);
        }

        protected void OnEscape(object sender, EventArgs e)
        {
            Initialize();
            InvokeAsync(StateHasChanged);
        }

        public void Dispose()
        {
            _debounceTimer.Dispose();
            Interop.OnEscapeEvent -= OnEscape;
            Interop.OnEscapeEvent -= OnFocusOut;
        }
    }
}
