using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management.Automation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using log4net;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using PowerShellTools.Classification;
using PowerShellTools.Common.ServiceManagement.IntelliSenseContract;
using PowerShellTools.Repl;
using Tasks = System.Threading.Tasks;

namespace PowerShellTools.Intellisense
{
    /// <summary>
    /// Class that is used for both the editor and the REPL window to manage the completion sources and
    /// completion session in the ITextBuffers. 
    /// </summary>
    internal class IntelliSenseManager
    {
        internal IOleCommandTarget NextCommandHandler;
        private readonly ITextView _textView;
        private readonly ICompletionBroker _broker;
        private ICompletionSession _activeSession;
        private readonly SVsServiceProvider _serviceProvider;
        private static readonly ILog Log = LogManager.GetLogger(typeof(IntelliSenseManager));
        private readonly bool _isRepl;
        private int _replacementIndexOffset;
        private IVsStatusbar _statusBar;
        private ITextSnapshotLine _completionLine;
        private int _completionCaretInLine;
        private string _completionText;
        private int _completionCaretPosition;
        private Stopwatch _sw;
        private long _triggerTag;

        public IntelliSenseManager(ICompletionBroker broker, SVsServiceProvider provider, IOleCommandTarget commandHandler, ITextView textView, IntelliSenseEventsHandlerProxy callbackContet)
        {
            _triggerTag = 0;
            _sw = new Stopwatch();
            _broker = broker;
            NextCommandHandler = commandHandler;
            _textView = textView;
            _isRepl = _textView.Properties.ContainsProperty(BufferProperties.FromRepl);
            _serviceProvider = provider;
            _statusBar = (IVsStatusbar)PowerShellToolsPackage.Instance.GetService(typeof(SVsStatusbar));
            callbackContet.CompletionListUpdated += IntelliSenseManager_CompletionListUpdated;
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            return NextCommandHandler.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
        }

        /// <summary>
        /// Main method used to determine how to handle keystrokes within a ITextBuffer.
        /// </summary>
        /// <param name="pguidCmdGroup"></param>
        /// <param name="nCmdId"></param>
        /// <param name="nCmdexecopt"></param>
        /// <param name="pvaIn"></param>
        /// <param name="pvaOut"></param>
        /// <returns></returns>
        public int Exec(ref Guid pguidCmdGroup, uint nCmdId, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            if (VsShellUtilities.IsInAutomationFunction(_serviceProvider) || 
                Utilities.IsInCommentArea(_textView.Caret.Position.BufferPosition.Position, _textView.TextBuffer))
            {
                return NextCommandHandler.Exec(ref pguidCmdGroup, nCmdId, nCmdexecopt, pvaIn, pvaOut);
            }

            //make a copy of this so we can look at it after forwarding some commands 
            var commandId = nCmdId;
            var typedChar = char.MinValue;
            //make sure the input is a char before getting it 

            if (pguidCmdGroup == VSConstants.VSStd2K && nCmdId == (uint)VSConstants.VSStd2KCmdID.TYPECHAR)
            {
                typedChar = (char)(ushort)Marshal.GetObjectForNativeVariant(pvaIn);
                Log.DebugFormat("Typed Character: '{0}'", (typedChar == char.MinValue) ? "<null>" : typedChar.ToString());

                if (_activeSession == null && 
                    IsNotIntelliSenseTriggerWhenInStringLiteral(typedChar) &&
                    Utilities.IsInStringArea(_textView.Caret.Position.BufferPosition.Position, _textView.TextBuffer))
                {
                    return NextCommandHandler.Exec(ref pguidCmdGroup, nCmdId, nCmdexecopt, pvaIn, pvaOut);
                }                
            }
            else
            {
                Log.DebugFormat("Non-TypeChar command: '{0}'", ToCommandName(pguidCmdGroup, nCmdId));
            }

            switch (nCmdId)
            {
                case (uint)VSConstants.VSStd2KCmdID.RETURN:
                case (uint)VSConstants.VSStd2KCmdID.TAB:
                    //check for a a selection 
                    if (_activeSession != null && !_activeSession.IsDismissed)
                    {
                        //if the selection is fully selected, commit the current session 
                        if (_activeSession.SelectedCompletionSet.SelectionStatus.IsSelected)
                        {
                            Log.Debug("Commit");
                            _activeSession.Commit();

                            //also, don't add the character to the buffer 
                            return VSConstants.S_OK;
                        }
                        else
                        {
                            Log.Debug("Dismiss");
                            //if there is no selection, dismiss the session
                            _activeSession.Dismiss();
                        }
                    }
                    else if (nCmdId == (uint)VSConstants.VSStd2KCmdID.TAB && _isRepl)
                    {
                        TriggerCompletion();
                        return VSConstants.S_OK;
                    }
                    break;

                case (uint)VSConstants.VSStd2KCmdID.COMPLETEWORD:
                    if (_activeSession != null && !_activeSession.IsDismissed)
                    {
                        if (_activeSession.CompletionSets[0].SelectionStatus.IsSelected)
                        {
                            _activeSession.Commit();
                        }
                    }
                    else
                    {
                        TriggerCompletion();
                    }
                    return VSConstants.S_OK;

                default:
                    break;
            }

            //check for a commit character 
            if (char.IsWhiteSpace(typedChar) && _activeSession != null && !_activeSession.IsDismissed)
            {
                // If user is typing a variable, SPACE shouldn't commit the selection. 
                // If the selection is fully matched with user's input, commit the current session and add the commit character to text buffer. 
                if (_activeSession.SelectedCompletionSet.SelectionStatus.IsSelected
                    && !_activeSession.SelectedCompletionSet.SelectionStatus.Completion.InsertionText.StartsWith("$", StringComparison.InvariantCulture))
                {
                    Log.Debug("Commit");
                    _activeSession.Commit();

                    bool isCompletionFullyMatched = false;
                    _textView.TextBuffer.Properties.TryGetProperty(BufferProperties.SessionCompletionFullyMatchedStatus, out isCompletionFullyMatched);
                    if (isCompletionFullyMatched)
                    {
                        // If user types all characters in a completion and click Space, then we should commit the selection and add the Space into text buffer.
                        return NextCommandHandler.Exec(ref pguidCmdGroup, nCmdId, nCmdexecopt, pvaIn, pvaOut);
                    }

                    //Don't add the character to the buffer if this commits the selection.
                    return VSConstants.S_OK;
                }
                else
                {
                    Log.Debug("Dismiss");
                    //if there is no selection, dismiss the session
                    _activeSession.Dismiss();
                }
            }

            if (IsBothIntelliSenseTriggerAndCommitChar(typedChar) && _activeSession != null && !_activeSession.IsDismissed)
            {
                //if the selection is fully selected, commit the current session but don't return. Instead, let it continues to trigger IntelliSense  
                if (_activeSession.SelectedCompletionSet.SelectionStatus.IsSelected)
                {
                    Log.Debug("Commit");
                    _activeSession.Commit();
                }
                else
                {
                    Log.Debug("Dismiss");
                    //if there is no selection, dismiss the session
                    _activeSession.Dismiss();
                }
            }

            // Check the char at caret before pass along the command
            // If command is backspace and completion session is active, then we need to see if the char to be deleted is an IntelliSense triggering char
            // If yes, then after deleting the char, we also dismiss the completion session
            // Otherwise, just filter the completion lists
            char charAtCaret = char.MinValue;
            if (commandId == (uint)VSConstants.VSStd2KCmdID.BACKSPACE && _activeSession != null && !_activeSession.IsDismissed)
            {
                int caretPosition = _textView.Caret.Position.BufferPosition.Position - 1;
                if (caretPosition >= 0)
                {
                    // caretPosition == -1 means caret is at the beginning of a file, which means no characters before it.
                    ITrackingPoint caretCharPosition = _textView.TextSnapshot.CreateTrackingPoint(caretPosition, PointTrackingMode.Positive);
                    charAtCaret = caretCharPosition.GetCharacter(_textView.TextSnapshot);
                }
            }

            // pass along the command so the char is added to the buffer 
            int retVal = NextCommandHandler.Exec(ref pguidCmdGroup, nCmdId, nCmdexecopt, pvaIn, pvaOut);
            bool handled = false;
            if (!typedChar.Equals(char.MinValue) && IsIntellisenseTrigger(typedChar))
            {
                // Make sure the completion session is dismissed when starting a new session
                if (_activeSession != null && !_activeSession.IsDismissed)
                {
                    _activeSession.Dismiss();
                }
                TriggerCompletion();
            }
            if (!typedChar.Equals(char.MinValue) && IsFilterTrigger(typedChar))
            {
                if (_activeSession != null)
                {
                    if (_activeSession.IsStarted)
                    {
                        try
                        {
                            Log.Debug("Filter");
                            _activeSession.Filter();
                        }
                        catch (Exception ex)
                        {
                            Log.Debug("Failed to filter session.", ex);
                        }
                    }
                }
            }
            else if (commandId == (uint)VSConstants.VSStd2KCmdID.BACKSPACE //redo the filter if there is a deletion
                     || commandId == (uint)VSConstants.VSStd2KCmdID.DELETE)
            {
                if (_activeSession != null && !_activeSession.IsDismissed)
                {
                    try
                    {
                        if (IsIntellisenseTrigger(charAtCaret))
                        {
                            Log.Debug("Dismiss");
                            _activeSession.Dismiss();
                        }
                        else
                        {
                            Log.Debug("Filter");
                            _activeSession.Filter();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug("Failed to filter session.", ex);
                    }
                }

                handled = true;
            }
            if (handled) return VSConstants.S_OK;
            return retVal;
        }

        /// <summary>
        /// A helper method to try and translate [for logging purposes] a visual studio cmdID to a usable string.
        /// </summary>
        /// <param name="cmdId">the cmdID</param>
        /// <returns>the cmdId if not in debug mode, or the command wasn't found.</returns>
        private static string ToCommandName(Guid commandGroup, uint commandId)
        {
#if DEBUG
            if (commandGroup == VSConstants.GUID_AppCommand)
            {
                return ((VSConstants.AppCommandCmdID)commandId).ToString();
            }
            else if (commandGroup == VSConstants.GUID_VSStandardCommandSet97)
            {
                return ((VSConstants.VSStd97CmdID)commandId).ToString();
            }
            else if (commandGroup == VSConstants.VSStd2K)
            {
                return ((VSConstants.VSStd2KCmdID)commandId).ToString();
            }
            else if (commandGroup == VSConstants.VsStd2010)
            {
                return ((VSConstants.VSStd2010CmdID)commandId).ToString();
            }
            else if (commandGroup == VSConstants.VsStd11)
            {
                return ((VSConstants.VSStd11CmdID)commandId).ToString();
            }
            else if (commandGroup == VSConstants.VsStd12)
            {
                return ((VSConstants.VSStd12CmdID)commandId).ToString();
            }

            // No Dev14 here, cause we want to continue to work before Dev14!
#endif
            return string.Format("CommandGroup: {0}, Id: {1}", commandGroup, commandId);
        }

        /// <summary>
        /// Triggers an IntelliSense session. This is done in a seperate thread than the UI to allow
        /// the user to continue typing. 
        /// </summary>
        private void TriggerCompletion()
        {
            _completionCaretPosition = (int)_textView.Caret.Position.BufferPosition;
            Tasks.Task.Factory.StartNew(() =>
            {
                try
                {
                    _completionLine = _textView.Caret.Position.BufferPosition.GetContainingLine();
                    _completionCaretInLine = (_completionCaretPosition - _completionLine.Start);
                    _completionText = _completionLine.GetText().Substring(0, _completionCaretInLine);
                    StartIntelliSense(_completionLine.Start, _completionCaretPosition, _completionText);
                }
                catch (Exception ex)
                {
                    Log.Warn("Failed to start IntelliSense", ex);
                }
            }); 
        }

        private void StartIntelliSense(int lineStartPosition, int caretPosition, string lineTextUpToCaret)
        {
            Interlocked.Increment(ref _triggerTag);

            if (_statusBar != null)
            {
                _statusBar.SetText("Running IntelliSense...");
            }

            _sw.Restart();

            // Procedures for correctly supporting IntelliSense in REPL window.
            // Step 1, determine if this is REPL windows IntelliSense. If no, continue with normal IntelliSense triggering process. Otherwise, continue with the following steps.            
            // Step 2, map the caret position in current REPL window text buffer to the one in current POWERSHELL text buffer.
            // Step 3, get the current POWERSHELL text.
            // Step 4, get the command completion results using the script text from Step 3 and the mapped caret position from Step 2.
            string script = String.Empty;
            int scriptParsePosition = 0;
            _replacementIndexOffset = 0; // This index offset is to caculate the existing text length minus the powershell code users are editing of Repl window.
            if (_textView.TextBuffer.ContentType.TypeName.Equals(PowerShellConstants.LanguageName, StringComparison.Ordinal))
            {
                script = _textView.TextBuffer.CurrentSnapshot.GetText();
                scriptParsePosition = caretPosition;
            }
            else if (_textView.TextBuffer.ContentType.TypeName.Equals(ReplConstants.ReplContentTypeName, StringComparison.Ordinal))
            {
                var currentActiveReplBuffer = _textView.BufferGraph.GetTextBuffers(p => p.ContentType.TypeName.Equals(PowerShellConstants.LanguageName, StringComparison.Ordinal))
                                                                   .LastOrDefault();
                var currentBufferPoint = _textView.BufferGraph.MapDownToBuffer(_textView.Caret.Position.BufferPosition,
                                                                               PointTrackingMode.Positive,
                                                                               currentActiveReplBuffer,
                                                                               PositionAffinity.Successor);
                scriptParsePosition = currentBufferPoint.Value.Position;
                script = currentActiveReplBuffer.CurrentSnapshot.GetText();

                _replacementIndexOffset = _textView.TextBuffer.CurrentSnapshot.GetText().Length - script.Length;
            }
            else
            {
                Log.Error("The content type of the text buffer isn't recognized.");
                return;
            }

            // Go out-of-proc here to get the completion list
            PowerShellToolsPackage.IntelliSenseService.RequestCompletionResults(script, scriptParsePosition, _triggerTag);
        }

        /// <summary>
        /// Post process the completion list got from powershell intellisense service
        /// Go back to the original text buffer and caret position so that we can show the completion window in the right place.
        /// </summary>
        /// <param name="sender">Intellisense service context</param>
        /// <param name="e">Completion list</param>
        private void IntelliSenseManager_CompletionListUpdated(object sender, EventArgs<CompletionResultList> e)
        {
            ThreadHelper.Generic.Invoke(() =>
                {
                    try
                    {
                        Log.Debug("Got new intellisense completion list");

                        var commandCompletion = e.Value;

                        IList<CompletionResult> completionMatchesList;
                        int completionReplacementIndex;
                        int completionReplacementLength;

                        if (commandCompletion == null)
                        {
                            return;
                        }
                        completionMatchesList = (from item in commandCompletion.CompletionMatches
                                                 select new CompletionResult(item.CompletionText,
                                                                             item.ListItemText,
                                                                             (CompletionResultType)item.ResultType,
                                                                             item.ToolTip)).ToList();

                        completionReplacementLength = commandCompletion.ReplacementLength;
                        completionReplacementIndex = commandCompletion.ReplacementIndex + _replacementIndexOffset;

                        var line = _textView.Caret.Position.BufferPosition.GetContainingLine();
                        var caretInLine = (_completionCaretPosition - line.Start);

                        int curCaretInLine = Math.Min(caretInLine, line.GetText().Length);
                        var text = line.GetText().Substring(0, curCaretInLine);
                        Log.Debug("Matching with existing caret position," + _completionCaretPosition.ToString());
                        if (string.Equals(_completionText, text, StringComparison.Ordinal) && completionMatchesList.Count != 0)
                        {
                            Log.Debug("Matched with existing caret position, updating intellisense UI");
                            if (completionMatchesList.Count != 0)
                            {
                                try
                                {
                                    IntellisenseDone(completionMatchesList,
                                                    _completionLine.Start,
                                                    completionReplacementIndex,
                                                    completionReplacementLength,
                                                    _completionCaretPosition);
                                }
                                catch (Exception ex)
                                {
                                    Log.Debug("Failed to start IntelliSense.", ex);
                                }
                            }
                        }

                        if (_statusBar != null)
                        {
                            _statusBar.SetText(String.Format("IntelliSense complete in {0:0.00} seconds...", _sw.Elapsed.TotalSeconds));
                        }

                        Log.Debug("Finishing process intellisense completion list!");
                    }
                    catch (Exception ex)
                    {
                        Log.Debug("Failed to process completion results.", ex);
                    }
                });
        }

        private void IntellisenseDone(IList<CompletionResult> completionResults, int lineStartPosition, int replacementIndex, int replacementLength, int startCaretPosition)
        {
            var textBuffer = _textView.TextBuffer;
            var length = replacementIndex - lineStartPosition;
            if (!SpanArgumentsAreValid(textBuffer.CurrentSnapshot, replacementIndex, replacementLength) || !SpanArgumentsAreValid(textBuffer.CurrentSnapshot, lineStartPosition, length))
            {
                return;
            }
            var lastWordReplacementSpan = textBuffer.CurrentSnapshot.CreateTrackingSpan(replacementIndex, replacementLength, SpanTrackingMode.EdgeInclusive);
            var lineUpToReplacementSpan = textBuffer.CurrentSnapshot.CreateTrackingSpan(lineStartPosition, length, SpanTrackingMode.EdgeExclusive);

            var triggerPoint = textBuffer.CurrentSnapshot.CreateTrackingPoint(startCaretPosition, PointTrackingMode.Positive);

            if (textBuffer.Properties.ContainsProperty(typeof(IList<CompletionResult>)))
            {
                textBuffer.Properties.RemoveProperty(typeof(IList<CompletionResult>));
            }

            if (textBuffer.Properties.ContainsProperty(BufferProperties.LastWordReplacementSpan))
            {
                textBuffer.Properties.RemoveProperty(BufferProperties.LastWordReplacementSpan);
            }

            if (textBuffer.Properties.ContainsProperty(BufferProperties.LineUpToReplacementSpan))
            {
                textBuffer.Properties.RemoveProperty(BufferProperties.LineUpToReplacementSpan);
            }

            textBuffer.Properties.AddProperty(typeof(IList<CompletionResult>), completionResults);
            textBuffer.Properties.AddProperty(BufferProperties.LastWordReplacementSpan, lastWordReplacementSpan);
            textBuffer.Properties.AddProperty(BufferProperties.LineUpToReplacementSpan, lineUpToReplacementSpan);

            Log.Debug("Dismissing all sessions...");

            if (Application.Current.Dispatcher.Thread == Thread.CurrentThread)
            {
                StartSession(triggerPoint);
            }
            else
            {
                Application.Current.Dispatcher.Invoke(() => StartSession(triggerPoint));
            }
        }

        private void StartSession(ITrackingPoint triggerPoint)
        {
            Log.Debug("Creating new completion session...");
            _broker.DismissAllSessions(_textView);
            _activeSession = _broker.CreateCompletionSession(_textView, triggerPoint, true);
            _activeSession.Properties.AddProperty(BufferProperties.SessionOriginIntellisense, "Intellisense");
            _activeSession.Dismissed += CompletionSession_Dismissed;
            _activeSession.Start();
            if (_activeSession.SelectedCompletionSet.Completions.Count > 0 && !_activeSession.SelectedCompletionSet.SelectionStatus.IsSelected)
            {
                _activeSession.SelectedCompletionSet.SelectionStatus = new CompletionSelectionStatus(_activeSession.SelectedCompletionSet.Completions[0], true, false);
            }
        }


        private void CompletionSession_Dismissed(object sender, EventArgs e)
        {
            Log.Debug("Session Dismissed.");
            _activeSession.Dismissed -= CompletionSession_Dismissed;
            _activeSession = null;
        }

        private static bool SpanArgumentsAreValid(ITextSnapshot snapshot, int start, int length)
        {
            return start >= 0 && length >= 0 && start + length <= snapshot.Length;
        }

        /// <summary>
        /// Determines whether a typed character should cause the completion source list to filter.
        /// </summary>
        /// <param name="ch">The typed character.</param>
        /// <returns>True if it is a filtering character.</returns>
        private static bool IsFilterTrigger(char ch)
        {
            Log.DebugFormat("IsFilterTrigger: [{0}]", ch);
            return (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');
        }

        /// <summary>
        /// Determines whether a typed character should cause the manager to trigger the intellisense drop down.
        /// </summary>
        /// <param name="ch">The typed character.</param>
        /// <returns>True if it is a triggering character.</returns>
        private static bool IsIntellisenseTrigger(char ch)
        {
            Log.DebugFormat("IsIntellisenseTrigger: [{0}]", ch);
            return ch == '-' || ch == '$' || ch == '.' || ch == ':' || ch == '\\';
        }

        private static bool IsBothIntelliSenseTriggerAndCommitChar(char ch)
        {
            Log.DebugFormat("IsBothIntelliSenseTriggerAndCommitChar: [{0}]", ch);
            return ch == '.';
        }

        private static bool IsNotIntelliSenseTriggerWhenInStringLiteral(char ch)
        {
            Log.DebugFormat("IsIntelliSenseTriggerInStringLiteral: [{0}]", ch);
            return ch == '-' || ch == '$' || ch == '.' || ch == ':';
        }
    }

    public class EventArgs<T> : EventArgs
    {
        public EventArgs(T value)
        {
            Value = value;
        }

        public T Value { get; private set; }
    }
}