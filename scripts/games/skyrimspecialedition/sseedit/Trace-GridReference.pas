unit UserScript;

{ Read-only, bounded Grid reference collector. Run with -autoload -Trace-GridReference. }

var
  Output, Status, QueryLines: TStringList;
  OutputPath, StatusPath, QueryPath: string;
  MaxTraversal, MaxRows, MaxOutputBytes, Traversed, RowsWritten, OutputBytes: Integer;
  LastPluginName, LastFormIdText, LastEditorIdText: string;
  LastRecord: IInterface;
  LastLookupValid: Boolean;

function Clean(const S: string): string;
begin
  Result := StringReplace(S, #9, ' ', [rfReplaceAll]);
  Result := StringReplace(Result, #13, ' ', [rfReplaceAll]);
  Result := StringReplace(Result, #10, ' ', [rfReplaceAll]);
end;

function StripOuterQuotes(const S: string): string;
begin
  Result := S;
  if (Length(Result) >= 2) and (Result[1] = '"') and
     (Result[Length(Result)] = '"') then
    Result := Copy(Result, 2, Length(Result) - 2);
end;

function GetArgumentValue(const Prefix: string): string;
var
  I: Integer;
  Value: string;
begin
  Result := '';
  for I := 1 to ParamCount do begin
    Value := ParamStr(I);
    if Pos(LowerCase(Prefix), LowerCase(Value)) = 1 then begin
      Result := StripOuterQuotes(Copy(Value, Length(Prefix) + 1, Length(Value)));
      Exit;
    end;
  end;
end;

function FieldAt(const S: string; Index: Integer): string;
var
  I, StartAt, Current: Integer;
begin
  Result := '';
  StartAt := 1;
  Current := 0;
  for I := 1 to Length(S) + 1 do begin
    if (I > Length(S)) or (Copy(S, I, 1) = #9) then begin
      if Current = Index then begin
        Result := Copy(S, StartAt, I - StartAt);
        Exit;
      end;
      Inc(Current);
      StartAt := I + 1;
    end;
  end;
end;

procedure WriteStatus(const Stage, Detail: string);
begin
  AddMessage('Grid reference collector: ' + Stage + ' - ' + Detail);
  if StatusPath = '' then Exit;
  Status.Add(Stage + #9 + Clean(Detail));
  Status.SaveToFile(StatusPath);
end;

function FileNameOf(E: IInterface): string;
begin
  Result := '';
  if Assigned(E) and Assigned(GetFile(E)) then Result := GetFileName(GetFile(E));
end;

function BoolValue(Value: Boolean): string;
begin
  if Value then Result := 'true' else Result := 'false';
end;

function FindContainingCell(E: IInterface): IInterface;
begin
  Result := nil;
  while Assigned(E) do begin
    if Signature(E) = 'CELL' then begin Result := E; Exit; end;
    E := GetContainer(E);
  end;
end;

function ContainingCellId(E: IInterface): string;
var
  Cell: IInterface;
begin
  Result := '';
  Cell := FindContainingCell(E);
  if Assigned(Cell) then Result := IntToHex(FixedFormID(MasterOrSelf(Cell)), 8);
end;

function FlattenElement(E: IInterface; const Prefix: string; Depth: Integer): string;
var
  I: Integer;
  Child: IInterface;
  ChildPath, Value, Part: string;
begin
  Result := '';
  if not Assigned(E) or (Depth > 12) then Exit;
  if ElementCount(E) = 0 then begin
    Value := GetEditValue(E);
    if Value <> '' then Result := Prefix + '=' + Clean(Value);
    Exit;
  end;
  for I := 0 to ElementCount(E) - 1 do begin
    Child := ElementByIndex(E, I);
    if Prefix = '' then ChildPath := Name(Child) else ChildPath := Prefix + '/' + Name(Child);
    Part := FlattenElement(Child, ChildPath, Depth + 1);
    if Part <> '' then begin
      if Result <> '' then Result := Result + '|';
      Result := Result + Part;
    end;
  end;
end;

function VmadDetail(E: IInterface): string;
var
  Vmad: IInterface;
begin
  Result := '';
  Vmad := ElementByPath(E, 'VMAD - Virtual Machine Adapter');
  if Assigned(Vmad) then Result := FlattenElement(Vmad, 'VMAD', 0);
end;

function Matches(E: IInterface; const FormIdText, EditorIdText: string): Boolean;
var
  FixedText: string;
begin
  Result := False;
  if not Assigned(E) then Exit;
  if FormIdText <> '' then begin
    FixedText := IntToHex(FixedFormID(MasterOrSelf(E)), 8);
    Result := SameText(FixedText, StringReplace(FormIdText, '0x', '', [rfIgnoreCase]));
    Exit;
  end;
  if EditorIdText <> '' then Result := SameText(EditorID(E), EditorIdText);
end;

function FindRecordInFile(F: IInterface; const FormIdText, EditorIdText: string): IInterface;
var
  I, FormIdValue: Integer;
  Candidate: IInterface;
  NormalizedFormId: string;
begin
  Result := nil;
  if not Assigned(F) then Exit;
  if FormIdText <> '' then begin
    NormalizedFormId := StringReplace(FormIdText, '0x', '', [rfIgnoreCase]);
    FormIdValue := StrToIntDef('$' + NormalizedFormId, -1);
    if FormIdValue <> -1 then begin
      Inc(Traversed);
      if Traversed > MaxTraversal then raise Exception.Create('Traversal safety limit exceeded.');
      Candidate := RecordByFormID(F, FormIdValue, True);
      if Assigned(Candidate) and Matches(Candidate, FormIdText, EditorIdText) then begin
        Result := Candidate;
        Exit;
      end;
    end;
    Exit;
  end;
  if EditorIdText = '' then Exit;
  for I := 0 to RecordCount(F) - 1 do begin
    Inc(Traversed);
    if Traversed > MaxTraversal then raise Exception.Create('Traversal safety limit exceeded.');
    Candidate := RecordByIndex(F, I);
    if Assigned(Candidate) and SameText(EditorID(Candidate), EditorIdText) then begin
      Result := Candidate;
      Exit;
    end;
  end;
end;

function FindRecord(const PluginName, FormIdText, EditorIdText: string): IInterface;
var
  I: Integer;
  F: IInterface;
begin
  Result := nil;
  if LastLookupValid and SameText(LastPluginName, PluginName) and
     SameText(LastFormIdText, FormIdText) and
     SameText(LastEditorIdText, EditorIdText) then begin
    Result := LastRecord;
    Exit;
  end;
  if PluginName <> '' then begin
    F := FileByName(PluginName);
    if Assigned(F) then Result := FindRecordInFile(F, FormIdText, EditorIdText);
  end else begin
    for I := 0 to FileCount - 1 do begin
      Result := FindRecordInFile(FileByIndex(I), FormIdText, EditorIdText);
      if Assigned(Result) then Break;
    end;
  end;
  LastPluginName := PluginName;
  LastFormIdText := FormIdText;
  LastEditorIdText := EditorIdText;
  LastRecord := Result;
  LastLookupValid := True;
end;

procedure AddRow(const QueryId, Operation, Claim, PluginName, FormIdText,
  EditorIdText, SignatureText, Origin, Winner, StateText, Detail: string);
var
  Row: string;
begin
  Inc(RowsWritten);
  if RowsWritten > MaxRows then raise Exception.Create('Output row safety limit exceeded.');
  Row := Clean(QueryId) + #9 + Clean(Operation) + #9 + Clean(Claim) + #9 +
    Clean(PluginName) + #9 + Clean(FormIdText) + #9 + Clean(EditorIdText) + #9 +
    Clean(SignatureText) + #9 + Clean(Origin) + #9 + Clean(Winner) + #9 +
    Clean(StateText) + #9 + Clean(Detail);
  OutputBytes := OutputBytes + Length(Row) + 2;
  if OutputBytes > MaxOutputBytes then raise Exception.Create('Output byte safety limit exceeded.');
  Output.Add(Row);
end;

procedure AuditEslEligibility(const QueryId, Operation, PluginName: string);
const
  EslMaximumNewRecords = $FFE;
  EslMaximumObjectId = $FFF;
var
  F, E: IInterface;
  I: Integer;
  NewRecordCount, MaximumObjectId, ObjectId: Cardinal;
  HasNewCell: Boolean;
  Eligibility, WarningText: string;
begin
  F := FileByName(PluginName);
  if not Assigned(F) then begin
    AddRow(QueryId, Operation, 'The requested plugin was not loaded.', PluginName,
      '', '', '', '', '', 'NotFound', 'Eligibility=PluginNotLoaded');
    Exit;
  end;
  if GetIsEsl(F) or SameText(ExtractFileExt(GetFileName(F)), '.esl') then begin
    AddRow(QueryId, Operation, 'The plugin already loads in a light slot.', PluginName,
      '', '', 'TES4', PluginName, PluginName, 'Found',
      'Eligibility=AlreadyLight;NewRecordCount=0;MaximumObjectId=000000;HasNewCell=false;HasEsmFlag=' + BoolValue(GetIsESM(F)));
    Exit;
  end;

  NewRecordCount := 0;
  MaximumObjectId := 0;
  HasNewCell := False;
  for I := 0 to Pred(RecordCount(F)) do begin
    Inc(Traversed);
    if Traversed > MaxTraversal then raise Exception.Create('Traversal safety limit exceeded.');
    E := RecordByIndex(F, I);
    if not IsMaster(E) or IsInjected(E) then Continue;
    if Signature(E) = 'CELL' then HasNewCell := True;
    Inc(NewRecordCount);
    ObjectId := FormID(E) and $FFFFFF;
    if ObjectId > MaximumObjectId then MaximumObjectId := ObjectId;
    if NewRecordCount > EslMaximumNewRecords then Break;
  end;

  WarningText := '';
  if HasNewCell and GetIsESM(F) then WarningText := 'EsmWithNewCellEngineRisk';
  if NewRecordCount > EslMaximumNewRecords then begin
    Eligibility := 'IneligibleTooManyNewRecords';
    AddRow(QueryId, Operation, 'The plugin exceeds the light-plugin new-record limit.', PluginName,
      '', '', 'TES4', PluginName, PluginName, 'Found',
      'Eligibility=' + Eligibility + ';NewRecordCount=' + IntToStr(NewRecordCount) +
      ';MaximumObjectId=' + IntToHex(MaximumObjectId, 6) + ';HasNewCell=' + BoolValue(HasNewCell) +
      ';HasEsmFlag=' + BoolValue(GetIsESM(F)) + ';Warning=' + WarningText);
    Exit;
  end;
  if MaximumObjectId <= EslMaximumObjectId then begin
    Eligibility := 'HeaderFlagOnly';
    AddRow(QueryId, Operation, 'The plugin is technically eligible for an ESL header flag without FormID compaction.', PluginName,
      '', '', 'TES4', PluginName, PluginName, 'Found',
      'Eligibility=' + Eligibility + ';NewRecordCount=' + IntToStr(NewRecordCount) +
      ';MaximumObjectId=' + IntToHex(MaximumObjectId, 6) + ';HasNewCell=' + BoolValue(HasNewCell) +
      ';HasEsmFlag=' + BoolValue(GetIsESM(F)) + ';Warning=' + WarningText);
  end else begin
    Eligibility := 'CompactionRequired';
    AddRow(QueryId, Operation, 'The plugin requires FormID compaction before it can receive an ESL header flag.', PluginName,
      '', '', 'TES4', PluginName, PluginName, 'Found',
      'Eligibility=' + Eligibility + ';NewRecordCount=' + IntToStr(NewRecordCount) +
      ';MaximumObjectId=' + IntToHex(MaximumObjectId, 6) + ';HasNewCell=' + BoolValue(HasNewCell) +
      ';HasEsmFlag=' + BoolValue(GetIsESM(F)) + ';Warning=' + WarningText);
  end;
end;

procedure ExecuteQuery(const Line: string);
var
  QueryId, Operation, PluginName, FormIdText, EditorIdText, Detail: string;
  E, Winner, Cell, RefRecord: IInterface;
  I: Integer;
begin
  QueryId := FieldAt(Line, 0);
  Operation := FieldAt(Line, 1);
  PluginName := FieldAt(Line, 2);
  FormIdText := FieldAt(Line, 3);
  EditorIdText := FieldAt(Line, 4);
  if SameText(Operation, 'AuditEslEligibility') then begin
    AuditEslEligibility(QueryId, Operation, PluginName);
    Exit;
  end;
  E := FindRecord(PluginName, FormIdText, EditorIdText);
  if not Assigned(E) then begin
    AddRow(QueryId, Operation, 'No matching record was found.', PluginName,
      FormIdText, EditorIdText, '', '', '', 'NotFound', '');
    Exit;
  end;
  Winner := WinningOverride(MasterOrSelf(E));
  if SameText(Operation, 'FindReferencesToBase') then begin
    if ReferencedByCount(MasterOrSelf(E)) = 0 then begin
      AddRow(QueryId, Operation, 'No references to the requested base record were found.',
        PluginName, FormIdText, EditorIdText, Signature(E),
        FileNameOf(MasterOrSelf(E)), FileNameOf(Winner), 'NotFound', '');
      Exit;
    end;
    for I := 0 to ReferencedByCount(MasterOrSelf(E)) - 1 do begin
      RefRecord := ReferencedByIndex(MasterOrSelf(E), I);
      AddRow(QueryId, Operation, 'A reference to the requested base record was found.',
        PluginName, IntToHex(FixedFormID(MasterOrSelf(RefRecord)), 8),
        EditorID(RefRecord), Signature(RefRecord), FileNameOf(MasterOrSelf(RefRecord)),
        FileNameOf(WinningOverride(MasterOrSelf(RefRecord))), 'Found', '');
    end;
    Exit;
  end;
  if SameText(Operation, 'TraceOverrideChain') then begin
    for I := 0 to OverrideCount(MasterOrSelf(E)) - 1 do begin
      RefRecord := OverrideByIndex(MasterOrSelf(E), I);
      AddRow(QueryId, Operation, 'An override-chain member was resolved.', PluginName,
        IntToHex(FixedFormID(MasterOrSelf(E)), 8), EditorID(RefRecord), Signature(RefRecord),
        FileNameOf(MasterOrSelf(E)), FileNameOf(Winner), 'Found',
        'Index=' + IntToStr(I) + ';Provider=' + FileNameOf(RefRecord));
    end;
    Exit;
  end;
  if SameText(Operation, 'InspectContainingCell') then begin
    Cell := FindContainingCell(E);
    if Assigned(Cell) then Detail := IntToHex(FixedFormID(MasterOrSelf(Cell)), 8)
    else Detail := '';
  end
  else if SameText(Operation, 'TraceOverrides') then
    Detail := 'OverrideCount=' + IntToStr(OverrideCount(MasterOrSelf(E)))
  else if SameText(Operation, 'InspectReferenceState') then
    Detail := 'Deleted=' + BoolValue(GetIsDeleted(E)) + ';Flags=' + GetElementEditValues(E, 'Record Header\Record Flags')
  else if SameText(Operation, 'InspectReferenceLinks') then
    Detail := 'NAME=' + GetElementEditValues(Winner, 'NAME - Base') +
      ';XTEL=' + GetElementEditValues(Winner, 'XTEL - Teleport Destination') +
      ';XESP=' + GetElementEditValues(Winner, 'XESP - Enable Parent') +
      ';Cell=' + ContainingCellId(Winner)
  else if SameText(Operation, 'InspectVmad') then
    Detail := VmadDetail(Winner)
  else if SameText(Operation, 'InspectScriptedReference') then
    Detail := 'Deleted=' + BoolValue(GetIsDeleted(Winner)) +
      ';Flags=' + GetElementEditValues(Winner, 'Record Header\Record Flags') +
      ';NAME=' + GetElementEditValues(Winner, 'NAME - Base') +
      ';XTEL=' + GetElementEditValues(Winner, 'XTEL - Teleport Destination') +
      ';XESP=' + GetElementEditValues(Winner, 'XESP - Enable Parent') +
      ';VMAD=' + VmadDetail(Winner)
  else Detail := '';
  AddRow(QueryId, Operation, 'The requested record was resolved.', PluginName,
    IntToHex(FixedFormID(MasterOrSelf(E)), 8), EditorID(E), Signature(E),
    FileNameOf(MasterOrSelf(E)), FileNameOf(Winner), 'Found', Detail);
end;

function Initialize: Integer;
var
  I: Integer;
begin
  Result := 0;
  Output := TStringList.Create;
  Status := TStringList.Create;
  QueryLines := TStringList.Create;
  OutputPath := GetArgumentValue('-gridoutput:');
  StatusPath := GetArgumentValue('-gridstatus:');
  QueryPath := GetArgumentValue('-gridquery:');
  if OutputPath = '' then OutputPath := GetEnvironmentVariable('GRID_HEALTH_OUTPUT');
  if StatusPath = '' then StatusPath := GetEnvironmentVariable('GRID_HEALTH_STATUS');
  if QueryPath = '' then QueryPath := GetEnvironmentVariable('GRID_HEALTH_QUERY');
  MaxTraversal := StrToIntDef(GetArgumentValue('-gridmaxtraversal:'), 0);
  MaxRows := StrToIntDef(GetArgumentValue('-gridmaxrows:'), 0);
  MaxOutputBytes := StrToIntDef(GetArgumentValue('-gridmaxbytes:'), 0);
  if MaxTraversal < 1 then MaxTraversal := StrToIntDef(GetEnvironmentVariable('GRID_HEALTH_MAX_TRAVERSAL'), 250000);
  if MaxRows < 1 then MaxRows := StrToIntDef(GetEnvironmentVariable('GRID_HEALTH_MAX_ROWS'), 100000);
  if MaxOutputBytes < 1 then MaxOutputBytes := StrToIntDef(GetEnvironmentVariable('GRID_HEALTH_MAX_OUTPUT_BYTES'), 33554432);
  Traversed := 0;
  RowsWritten := 0;
  OutputBytes := 0;
  LastPluginName := '';
  LastFormIdText := '';
  LastEditorIdText := '';
  LastRecord := nil;
  LastLookupValid := False;
  Status.Add('Stage'#9'Detail');
  WriteStatus('Initialize', 'Collector entered Initialize.');
  if (OutputPath = '') or (QueryPath = '') then begin
    WriteStatus('ConfigurationError', 'Required case-local environment paths are absent.');
    Result := 1;
    Exit;
  end;
  QueryLines.LoadFromFile(QueryPath);
  if QueryLines.Count < 2 then begin
    WriteStatus('QueryError', 'No queries were supplied.');
    Result := 1;
    Exit;
  end;
  Output.Add('QueryId'#9'Operation'#9'Claim'#9'Plugin'#9'FormId'#9'EditorId'#9'Signature'#9'Origin'#9'Winner'#9'State'#9'Detail');
  WriteStatus('Collecting', IntToStr(QueryLines.Count - 1) + ' bounded query or queries.');
  try
    for I := 1 to QueryLines.Count - 1 do ExecuteQuery(QueryLines[I]);
    Output.SaveToFile(OutputPath);
    WriteStatus('Complete', IntToStr(RowsWritten) + ' evidence row or rows written.');
  except
    on E: Exception do begin
      WriteStatus('CollectorError', E.Message);
      Result := 1;
    end;
  end;
end;

function Finalize: Integer;
begin
  Result := 0;
  if Assigned(Output) then Output.Free;
  if Assigned(Status) then Status.Free;
  if Assigned(QueryLines) then QueryLines.Free;
end;

end.
