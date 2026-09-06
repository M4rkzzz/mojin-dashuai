"""NAS read-only activity totals. No account names, tokens, or raw player records printed."""
import collections,json,subprocess

def rows(sql):
 p=subprocess.run(['docker','exec','-i','mc-client-hub-postgres-1','psql','-U','hub','-d','hub','-At','-v','ON_ERROR_STOP=1'],input=sql,capture_output=True,text=True,check=True)
 return [json.loads(x) for x in p.stdout.splitlines() if x.strip()]
report={w:dict(participants=0,claimedDays=0,ticketsHeld=0,medalsHeld=0,rareAwards=0,pendingDeliveries=0,deliveredItems={}) for w in ['m3e','dc2','mb','vw']}
for a in rows('SELECT "StateJson" FROM "ActivityAccounts";'):
 for w,s in a.get('worlds',{}).items():
  if w not in report:continue
  r=report[w];r['participants']+=1;r['claimedDays']+=sum(1 for day in a.get('dailyClaims',{}) if any(x.get('source','').startswith(day+' ') for x in s.get('awards',[])))
  r['ticketsHeld']+=s.get('tickets',0);r['medalsHeld']+=s.get('medals',0);r['rareAwards']+=sum(x.get('tier')=='rare' for x in s.get('awards',[]))
for d in rows('''SELECT json_build_object('world',"Instance",'items',"ItemsJson"::json,'delivered',"AppliedAt" IS NOT NULL) FROM "ActivityDeliveries";'''):
 r=report[d['world']]
 if not d['delivered']:r['pendingDeliveries']+=1;continue
 for i in d['items']:
  key=i['id']+'@'+str(i['meta']);r['deliveredItems'][key]=r['deliveredItems'].get(key,0)+i['count']
print(json.dumps(report,ensure_ascii=False,indent=2))
