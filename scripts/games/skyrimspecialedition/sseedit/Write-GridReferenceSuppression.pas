unit UserScript;

{ Bounded GRID writer. Creates one new patch and sets Initially Disabled on
  the exact reviewed winning REFR/ACHR records supplied in a case-local TSV. }

var
  Specification, Receipt, Status: TStringList;
  SpecificationPath, ReceiptPath, StatusPath, PatchName, ExpectedSpecificationHash: string;
  PatchFile: IInterface;
  AppliedCount: Integer;

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
  AddMessage('GRID reference suppression writer: ' + Stage + ' - ' + Detail);
  Status.Add(Stage + #9 + Clean(Detail));
  Status.SaveToFile(StatusPath);
end;

procedure Fail(const Code, Detail: string);
begin
  Receipt.Add('Failed' + #9 + Clean(Code) + #9 + Clean(Detail));
  Receipt.SaveToFile(ReceiptPath);
  WriteStatus('Failed', Code + ': ' + Detail);
  raise Exception.Create(Code + ': ' + Detail);
end;

procedure ApplyReference(const Line: string; LineNumber: Integer);
var
  OriginPlugin, FormIdText, ExpectedWinner, ExpectedSignature: string;
  FormIdValue: Integer;
  SourceFile, SourceRecord, Winner, CopiedRecord: IInterface;
begin
  OriginPlugin := FieldAt(Line, 0);
  FormIdText := FieldAt(Line, 1);
  ExpectedWinner := FieldAt(Line, 2);
  ExpectedSignature := FieldAt(Line, 3);
  if (OriginPlugin = '') or (FormIdText = '') or (ExpectedWinner = '') or
     ((ExpectedSignature <> 'REFR') and (ExpectedSignature <> 'ACHR')) then begin
    Fail('MalformedTarget', 'line ' + IntToStr(LineNumber));
    Exit;
  end;
  FormIdValue := StrToIntDef('$' + FormIdText, -1);
  if (FormIdValue < 0) or (FormIdValue > $FFFFFF) then begin
    Fail('InvalidLocalFormId', FormIdText);
    Exit;
  end;
  SourceFile := FileByName(OriginPlugin);
  if not Assigned(SourceFile) then begin
    Fail('SourcePluginNotLoaded', OriginPlugin);
    Exit;
  end;
  SourceRecord := RecordByFormID(SourceFile, FormIdValue, True);
  if not Assigned(SourceRecord) then begin
    Fail('SourceRecordNotFound', OriginPlugin + ':' + FormIdText);
    Exit;
  end;
  SourceRecord := MasterOrSelf(SourceRecord);
  Winner := WinningOverride(SourceRecord);
  if not Assigned(Winner) then begin
    Fail('WinningOverrideNotFound', OriginPlugin + ':' + FormIdText);
    Exit;
  end;
  if not SameText(GetFileName(GetFile(Winner)), ExpectedWinner) then begin
    Fail('WinningPluginChanged', GetFileName(GetFile(Winner)));
    Exit;
  end;
  if Signature(Winner) <> ExpectedSignature then begin
    Fail('WinningSignatureChanged', Signature(Winner));
    Exit;
  end;
  if GetIsDeleted(Winner) or GetIsInitiallyDisabled(Winner) then begin
    Fail('WinningStateChanged', OriginPlugin + ':' + FormIdText);
    Exit;
  end;
  if Assigned(ElementByPath(Winner, 'XESP - Enable Parent')) then begin
    Fail('EnableParentHazard', OriginPlugin + ':' + FormIdText);
    Exit;
  end;
  AddRequiredElementMasters(Winner, PatchFile, False, True);
  CopiedRecord := wbCopyElementToFile(Winner, PatchFile, False, True);
  if not Assigned(CopiedRecord) then begin
    Fail('OverrideCopyFailed', OriginPlugin + ':' + FormIdText);
    Exit;
  end;
  if not SetIsInitiallyDisabled(CopiedRecord, True) then begin
    Fail('InitiallyDisabledWriteFailed', OriginPlugin + ':' + FormIdText);
    Exit;
  end;
  if not GetIsInitiallyDisabled(CopiedRecord) or GetIsDeleted(CopiedRecord) then begin
    Fail('OverridePostconditionFailed', OriginPlugin + ':' + FormIdText);
    Exit;
  end;
  Inc(AppliedCount);
  Receipt.Add('Applied' + #9 + Clean(OriginPlugin) + #9 + FormIdText + #9 +
    Clean(ExpectedWinner) + #9 + ExpectedSignature + #9 +
    GetFileName(GetFile(CopiedRecord)) + #9 + 'InitiallyDisabled=true');
end;

function Initialize: Integer;
var
  I: Integer;
  Header: string;
begin
  Result := 1;
  AppliedCount := 0;
  Specification := TStringList.Create;
  Receipt := TStringList.Create;
  Status := TStringList.Create;
  SpecificationPath := GetArgumentValue('-gridspec:');
  ReceiptPath := GetArgumentValue('-gridreceipt:');
  StatusPath := GetArgumentValue('-gridstatus:');
  PatchName := GetArgumentValue('-gridpatch:');
  ExpectedSpecificationHash := GetArgumentValue('-gridspechash:');
  if (SpecificationPath = '') or (ReceiptPath = '') or (StatusPath = '') or
     (PatchName = '') or (ExpectedSpecificationHash = '') then begin
    AddMessage('GRID reference suppression writer: required bounded arguments are missing.');
    Exit;
  end;
  Specification.LoadFromFile(SpecificationPath);
  if Specification.Count < 2 then begin
    WriteStatus('Failed', 'SpecificationEmpty');
    Exit;
  end;
  Header := Specification[0];
  if (FieldAt(Header, 0) <> 'GRID_REFERENCE_SUPPRESSION') or
     (FieldAt(Header, 1) <> '1') or
     not SameText(FieldAt(Header, 2), PatchName) or
     not SameText(FieldAt(Header, 3), ExpectedSpecificationHash) then begin
    WriteStatus('Failed', 'SpecificationHeaderMismatch');
    Exit;
  end;
  if Assigned(FileByName(PatchName)) then begin
    WriteStatus('Failed', 'PatchAlreadyLoaded');
    Exit;
  end;
  PatchFile := AddNewFileName(PatchName);
  if not Assigned(PatchFile) then begin
    WriteStatus('Failed', 'PatchCreationFailed');
    Exit;
  end;
  Receipt.Add('GRID_REFERENCE_SUPPRESSION_RECEIPT' + #9 + '1' + #9 +
    Clean(PatchName) + #9 + Clean(ExpectedSpecificationHash));
  WriteStatus('Applying', IntToStr(Specification.Count - 1) + ' exact reference(s)');
  for I := 1 to Specification.Count - 1 do
    ApplyReference(Specification[I], I + 1);
  Receipt.Add('Completed' + #9 + IntToStr(AppliedCount));
  Receipt.SaveToFile(ReceiptPath);
  WriteStatus('Completed', IntToStr(AppliedCount) + ' exact reference override(s)');
  Result := 0;
end;

function Finalize: Integer;
begin
  Result := 0;
  if Assigned(Specification) then Specification.Free;
  if Assigned(Receipt) then Receipt.Free;
  if Assigned(Status) then Status.Free;
end;

end.
