import fs from 'node:fs/promises';
import { FileBlob, SpreadsheetFile, Workbook } from '@oai/artifact-tool';

const sourcePath = 'C:/Users/chaitanachote/Downloads/EmployeeMigrationTemplate.xlsx';
const outputDir = 'C:/project reference/HR-Project/outputs/employee-migration-template-20260925';
const source = await SpreadsheetFile.importXlsx(await FileBlob.load(sourcePath));
const sourceData = source.worksheets.getItemAt(0);
const sourceGuide = source.worksheets.getItemAt(1);
const removed = new Set(['รหัสบริษัท', 'ฝ่าย', 'ส่วนงาน', 'รหัสตำแหน่ง']);
const sourceHeaders = sourceData.getRange('A1:FT1').values[0];
const headers = sourceHeaders.filter(value => !removed.has(value));
if (sourceHeaders.length !== 176 || headers.length !== 172) throw new Error('Unexpected template headers');
const guideRows = sourceGuide.getRange('A1:E194').values;
const dictionary = guideRows.slice(18).filter(row => !removed.has(row[1]));
if (dictionary.length !== 172) throw new Error(`Unexpected dictionary length ${dictionary.length}`);

const workbook = Workbook.create();
const data = workbook.worksheets.add(sourceData.name);
const guide = workbook.worksheets.add(sourceGuide.name);
data.showGridLines = true;
guide.showGridLines = true;
data.getRangeByIndexes(0, 0, 1, headers.length).values = [headers];
const dataHeader = data.getRangeByIndexes(0, 0, 1, headers.length);
dataHeader.format.fill = '#1C3153';
dataHeader.format.font = {name:'Arial', size:10, bold:true, color:'#FFFFFF'};
dataHeader.format.rowHeight = 46;
dataHeader.format.columnWidth = 28;
dataHeader.format.wrapText = true;
dataHeader.format.verticalAlignment = 'center';
dataHeader.format.borders = {bottom:{style:'medium',color:'#F36B35'}};
data.freezePanes.freezeRows(1);
data.getRangeByIndexes(1, 0, 100, headers.length).setNumberFormat('@');

function colName(index) {
  let n=index+1, out='';
  while(n){n--;out=String.fromCharCode(65+n%26)+out;n=Math.floor(n/26)}
  return out;
}
for (const sourceColumn of ['B','Z','AF','BV','BW','BX','CN','CR']) {
  const oldIndex = sourceColumn.split('').reduce((n,c)=>n*26+c.charCodeAt(0)-64,0)-1;
  const label = sourceHeaders[oldIndex];
  const newIndex = headers.indexOf(label);
  if (newIndex < 0) throw new Error(`Missing validation field ${label}`);
  data.getRange(`${colName(newIndex)}2:${colName(newIndex)}101`).dataValidation = {rule:{type:'list',values:['ใช่','ไม่ใช่']}};
}

guide.getRange('A1:E18').values = guideRows.slice(0,18);
guide.getRangeByIndexes(18,0,dictionary.length,5).values = dictionary;
guide.getRange('A2').format.font = {name:'Arial', size:15, bold:true, color:'#1C3153'};
guide.getRange('A4:A17').format.font = {name:'Arial', size:10, bold:true, color:'#1C3153'};
guide.getRange('B4:B17').format.font = {name:'Arial', size:10, color:'#27364F'};
guide.getRange('A18:E18').format.fill = '#1C3153';
guide.getRange('A18:E18').format.font = {name:'Arial', size:10, bold:true, color:'#FFFFFF'};
guide.getRange('A18:E18').format.rowHeight = 26;
guide.getRangeByIndexes(18,0,dictionary.length,5).format.font = {name:'Arial', size:10, color:'#27364F'};
guide.getRange('A:A').format.columnWidth = 25;
guide.getRange('B:B').format.columnWidth = 65;
guide.getRange('C:C').format.columnWidth = 36;
guide.getRange('D:D').format.columnWidth = 20;
guide.getRange('E:E').format.columnWidth = 45;
guide.freezePanes.freezeRows(18);

workbook.recalculate();
const preview = await workbook.render({sheetName:data.name,range:'AP1:AX6',scale:1.5,format:'png'});
await fs.writeFile('C:/project reference/HR-Project/.artifact-work/employee-template-fix/after.png',new Uint8Array(await preview.arrayBuffer()));
console.log('RESULT',headers.length,dictionary.length,headers.slice(40,49));
console.log('ERRORS',(await workbook.inspect({kind:'match',searchTerm:'#REF!|#DIV/0!|#VALUE!|#NAME\\?|#N/A|#NUM!|#NULL!|#SPILL!|#CALC!',options:{useRegex:true,maxResults:20},maxChars:1500})).ndjson);
await fs.mkdir(outputDir,{recursive:true});
const result = await SpreadsheetFile.exportXlsx(workbook);
await result.save(`${outputDir}/EmployeeMigrationTemplate.xlsx`);
console.log('EXPORTED',`${outputDir}/EmployeeMigrationTemplate.xlsx`);
