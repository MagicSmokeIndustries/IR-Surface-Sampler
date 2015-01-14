using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using UnityEngine;

namespace IRSurfaceSampler
{
	public class ModuleIRSurfaceSampler : ModuleScienceExperiment, IScienceDataContainer
	{
		[KSPField]
		public string drillTransform;
		[KSPField]
		public float drillDistance;
		[KSPField]
		public string animationName;

		private Animation anim;
		private float scale;
		private float lastUpdate = 0f;
		private float updateInterval = 0.5f;
		private Transform t;
		private ScienceExperiment surfaceExp;
		private ScienceExperiment asteroidExp;
		private List<ScienceData> dataList = new List<ScienceData>();

		private const string potato = "PotatoRoid";
		private const string asteroidExperimentID = "asteroidSample";

		public override void OnStart(PartModule.StartState state)
		{
			if (!string.IsNullOrEmpty(animationName))
				anim = part.FindModelAnimators(animationName)[0];
			if (!string.IsNullOrEmpty(experimentID))
				surfaceExp = ResearchAndDevelopment.GetExperiment(experimentID);
			if (!string.IsNullOrEmpty(asteroidExperimentID))
				asteroidExp = ResearchAndDevelopment.GetExperiment(asteroidExperimentID);
			if (!string.IsNullOrEmpty(experimentActionName))
			{
				Events["DeployExperiment"].guiName = experimentActionName;
				Actions["DeployAction"].guiName = experimentActionName;
				Actions["DeployAction"].active = useActionGroups;
			}
		}

		public override void OnSave(ConfigNode node)
		{
			node.RemoveNodes("ScienceData");
			foreach (ScienceData storedData in dataList)
			{
				ConfigNode storedDataNode = node.AddNode("ScienceData");
				storedData.Save(storedDataNode);
			}
		}

		public override void OnLoad(ConfigNode node)
		{
			if (node.HasNode("ScienceData"))
			{
				foreach (ConfigNode storedDataNode in node.GetNodes("ScienceData"))
				{
					ScienceData data = new ScienceData(storedDataNode);
					dataList.Add(data);
				}
			}
		}

		//Update the KSPEvents and check for asteroids nearby; run only twice per second
		private void Update()
		{
			if (HighLogic.LoadedSceneIsFlight)
			{
				float deltaTime = 1f;
				if (Time.deltaTime != 0)
					deltaTime = TimeWarp.deltaTime / Time.deltaTime;
				if (deltaTime > 10)
					deltaTime = 10;
				if (((Time.time * deltaTime) - lastUpdate) > updateInterval)
				{
					lastUpdate = Time.time;
					if (asteroidInRange() && !Deployed)
						Events["DeployExperiment"].active = true;
					else if ((vessel.situation == Vessel.Situations.LANDED || vessel.situation == Vessel.Situations.SPLASHED || vessel.situation == Vessel.Situations.PRELAUNCH) && !Deployed)
						Events["DeployExperiment"].active = true;
					else
						Events["DeployExperiment"].active = false;
				}
				eventsCheck();
			}
		}

		//We update all the KSPEvents to make sure they are available when they should be
		private void eventsCheck()
		{
			Events["ResetExperiment"].active = dataList.Count > 0;
			Events["ReviewDataEvent"].active = dataList.Count > 0;
			Events["ResetExperimentExternal"].active = dataList.Count > 0;
			Events["CollectDataExternalEvent"].active = dataList.Count > 0;
		}

		//This overrides the base Deploy Experiment event, if the drill distance checks out it starts a timer to begin the experiment
		new public void DeployExperiment()
		{
			ModuleAsteroid modAst;

			if (!Deployed)
			{
				if (drillDistanceCheck(out modAst))
				{
					drillAnimate();
					StartCoroutine(waitForDeploy(modAst));
				}
				else
					ScreenMessages.PostScreenMessage("IR Surface Sample Drill is not close enough for sample collection", 5f, ScreenMessageStyle.UPPER_CENTER);
			}
			else
				ScreenMessages.PostScreenMessage("Cannot collect any more samples", 5f, ScreenMessageStyle.UPPER_CENTER);
		}

		new public void DeployAction(KSPActionParam actParams)
		{
			DeployExperiment();
		}

		//A timer to prevent the experiment results from being displayed right away
		private IEnumerator waitForDeploy(ModuleAsteroid m)
		{
			float time = 2f;
			if (anim != null)
				time = anim[animationName].length;
			yield return new WaitForSeconds(time);
			ScienceData data = sampleData(m);
			if (data != null)
			{
				dataList.Add(data);
				ReviewData();
				Deployed = true;
			}
			else
				Debug.LogWarning("[IRSurfaceSampler] Something went wrong with Science Data collection here...");
		}

		//Here we check to see if the current vessel or target vessel has an asteroid attached
		private bool asteroidInRange()
		{
			if (vessel.FindPartModulesImplementing<ModuleAsteroid>().Count > 0)
				return true;

			Vessel targetVessel = FlightGlobals.fetch.VesselTarget as Vessel;
			FlightCoMTracker targetObj = FlightGlobals.fetch.VesselTarget as FlightCoMTracker;

			if (targetVessel != null)
			{
				List<ModuleAsteroid> asteroids = targetVessel.FindPartModulesImplementing<ModuleAsteroid>();
				if (asteroids.Count > 0)
				{
					foreach (ModuleAsteroid astMod in asteroids)
					{
						Vector3d astPos = astMod.part.transform.position;
						Vector3d pPos = part.transform.position;
						if ((astPos - pPos).magnitude < 200)
							return true;
					}
				}
			}
			else if (targetObj != null)
			{
				List<ModuleAsteroid> asteroids = targetObj.GetOrbitDriver().vessel.FindPartModulesImplementing<ModuleAsteroid>();
				if (asteroids.Count > 0)
				{
					foreach (ModuleAsteroid astMod in asteroids)
					{
						Vector3d astPos = astMod.part.transform.position;
						Vector3d pPos = part.transform.position;
						if ((astPos - pPos).magnitude < 200)
							return true;
					}
				}
			}

			return false;
		}

		//A method to check the distance from the drill transform to the ground or to an asteroid surface.
		//It returns a ModuleAsteroid instance if an asteroid is struck by the drill, which is needed to generate
		//the asteroid sample data.
		private bool drillDistanceCheck(out ModuleAsteroid m)
		{
			if (!string.IsNullOrEmpty(drillTransform))
				t = part.FindModelTransform(drillTransform);
			if (!float.IsNaN(drillDistance))
				scale = drillDistance * part.rescaleFactor;

			m = null;

			if (t == null || float.IsNaN(scale) || scale <= 0f)
			{
				Debug.LogError("[IRSurfaceSampler] Something went wrong while assigning the drill transform or distance value; check the part config file");
				return false;
			}

			RaycastHit hit = new RaycastHit();
			Vector3 pos = t.position;
			Ray ray = new Ray(pos, t.forward);

			//This section handles any changes in the part's size by TweakScale
			//If the TweakScale module uses the free resize option it may need to be altered
			//because this reports the scale as a percentage, rather than an multiplier (I think)
			if (part.Modules.Contains("TweakScale"))
			{
				PartModule pM = part.Modules["TweakScale"];
				if (pM.Fields.GetValue("currentScale") != null)
				{
					float tweakedScale = 1f;
					try
					{
						tweakedScale = pM.Fields.GetValue<float>("currentScale");
					}
					catch (Exception e)
					{
						Debug.LogError("[IRSurfaceSampler] Error in detecting TweakScale component; reset distance scale to 1 : " + e);
					}
					scale *= tweakedScale;
				}
			}

			Physics.Raycast(ray, out hit, scale);

			if (hit.collider != null)
			{
				if (hit.collider.attachedRigidbody != null)
				{
					string obj = hit.collider.attachedRigidbody.gameObject.name;
					if (!string.IsNullOrEmpty(obj))
					{
						if (obj.StartsWith(potato))
						{
							Part p = Part.FromGO(hit.transform.gameObject) ?? hit.transform.gameObject.GetComponentInParent<Part>();

							if (p != null)
							{
								if (p.Modules.Contains("ModuleAsteroid"))
								{
									m = p.FindModuleImplementing<ModuleAsteroid>();
									return true;
								}
							}
						}
					}
				}

				Transform hitT = hit.collider.transform;
				int i = 0;

				//This loop keeps moving up the chain looking for a transform with a name that matches the current celestial body's name; it stops at a certain point
				while (hitT != null && i < 200)
				{
					if (hitT.name.Contains(vessel.mainBody.name))
						return true;
					hitT = hitT.parent;
					i++;
				}
			}

			return false;
		}

		//A simple animation method
		private void drillAnimate()
		{
			if (anim != null)
			{
				if (!anim.IsPlaying(animationName))
				{
					anim[animationName].speed = 1f;
					anim[animationName].normalizedTime = 0f;
					anim.Blend(animationName, 1f);
				}
			}
		}

		//This method handles generating the surface or asteroid sample ScienceData
		private ScienceData sampleData(ModuleAsteroid m)
		{
			ScienceExperiment exp = null;
			ScienceSubject sub = null;
			ExperimentSituations expSit;
			ScienceData data = null;
			string biome = "";

			if (m != null)
				exp = asteroidExp;
			else
				exp = surfaceExp;

			if (exp == null)
				return null;

			expSit = ScienceUtil.GetExperimentSituation(vessel);

			if (exp.IsAvailableWhile(expSit, vessel.mainBody))
			{
				if (exp.BiomeIsRelevantWhile(expSit))
				{
					if (!string.IsNullOrEmpty(vessel.landedAt))
						biome = Vessel.GetLandedAtString(vessel.landedAt);
					else
						biome = ScienceUtil.GetExperimentBiome(vessel.mainBody, vessel.latitude, vessel.longitude);
				}
			}

			if (m != null)
				sub = ResearchAndDevelopment.GetExperimentSubject(exp, expSit, m.part.partInfo.name + m.part.flightID, m.part.partInfo.title, vessel.mainBody, biome);
			else
				sub = ResearchAndDevelopment.GetExperimentSubject(exp, expSit, vessel.mainBody, biome);

			if (sub == null)
				return null;

			data = new ScienceData(exp.baseValue * exp.dataScale, this.xmitDataScalar, 1f, sub.id, sub.title);

			return data;
		}

		/* These methods handle the basic KSPEvents and EVA events for reviewing and discarding data */

		new public void ReviewData()
		{
			newResultPage();
		}

		new public void ReviewDataEvent()
		{
			ReviewData();
		}

		new public void ResetExperiment()
		{
			if (dataList.Count > 0)
			{
				dataList.Clear();
				Deployed = false;
			}
		}

		new public void ResetAction(KSPActionParam param)
		{
			ResetExperiment();
		}

		new public void ResetExperimentExternal()
		{
			ResetExperiment();
		}

		new public void CollectDataExternalEvent()
		{
			List<ModuleScienceContainer> EVACont = FlightGlobals.ActiveVessel.FindPartModulesImplementing<ModuleScienceContainer>();
			if (dataList.Count > 0)
			{
				if (EVACont.First().StoreData(new List<IScienceDataContainer> { this }, false))
					foreach (ScienceData data in dataList)
						DumpData(data);
			}
		}

		/* These methods handle generating and interacting with the science results page */

		private void newResultPage()
		{
			if (dataList.Count > 0)
			{
				ScienceData data = dataList[0];
				ExperimentResultDialogPage page = new ExperimentResultDialogPage(part, data, data.transmitValue, xmitDataScalar / 2f, !rerunnable, transmitWarningText, true, data.labBoost < 1 && checkLabOps() && xmitDataScalar < 1, new Callback<ScienceData>(onDiscardData), new Callback<ScienceData>(onKeepData), new Callback<ScienceData>(onTransmitData), new Callback<ScienceData>(onSendToLab));
				ExperimentsResultDialog.DisplayResult(page);
			}
		}

		private void onDiscardData(ScienceData data)
		{
			if (dataList.Count > 0)
			{
				dataList.Remove(data);
				Deployed = false;
			}
		}

		private void onKeepData(ScienceData data)
		{
		}

		private void onTransmitData(ScienceData data)
		{
			List<IScienceDataTransmitter> tranList = vessel.FindPartModulesImplementing<IScienceDataTransmitter>();
			if (tranList.Count > 0 && dataList.Count > 0)
			{
				tranList.OrderBy(ScienceUtil.GetTransmitterScore).First().TransmitData(new List<ScienceData> { data });
				DumpData(data);
			}
			else
				ScreenMessages.PostScreenMessage("No transmitters available on this vessel.", 4f, ScreenMessageStyle.UPPER_LEFT);
		}

		private void onSendToLab(ScienceData data)
		{
			List<ModuleScienceLab> labList = vessel.FindPartModulesImplementing<ModuleScienceLab>();
			if (checkLabOps() && dataList.Count > 0)
				labList.OrderBy(ScienceUtil.GetLabScore).First().StartCoroutine(labList.First().ProcessData(data, new Callback<ScienceData>(onComplete)));
			else
				ScreenMessages.PostScreenMessage("No operational lab modules on this vessel. Cannot analyze data.", 4f, ScreenMessageStyle.UPPER_CENTER);
		}

		private void onComplete(ScienceData data)
		{
			ReviewData();
		}

		private bool checkLabOps()
		{
			List<ModuleScienceLab> labList = vessel.FindPartModulesImplementing<ModuleScienceLab>();
			for (int i = 0; i < labList.Count; i++)
			{
				if (labList[i].IsOperational())
					return true;
			}
			return false;
		}

		/* These methods handle the IScienceDataContainer Interface which is used by external modules to access
		 * the science data stored in this part */

		ScienceData[] IScienceDataContainer.GetData()
		{
			return dataList.ToArray();
		}

		int IScienceDataContainer.GetScienceCount()
		{
			return dataList.Count;
		}

		bool IScienceDataContainer.IsRerunnable()
		{
			return IsRerunnable();
		}

		void IScienceDataContainer.ReviewData()
		{
			ReviewData();
		}

		void IScienceDataContainer.ReviewDataItem(ScienceData data)
		{
			ReviewData();
		}

		void IScienceDataContainer.DumpData(ScienceData data)
		{
			DumpData(data);
		}

		new private void DumpData(ScienceData data)
		{
			if (dataList.Contains(data))
			{
				Inoperable = !IsRerunnable();
				if (!Inoperable)
					Deployed = false;
				dataList.Remove(data);
			}
		}

		new private bool IsRerunnable()
		{
			if (rerunnable)
				return true;
			else
				return false;
		}

	}
}
