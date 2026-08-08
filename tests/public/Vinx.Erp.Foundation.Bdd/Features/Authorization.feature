@foundation
Feature: Foundation authorization

Scenario: Capability map without an explicit company is denied
  Given Ana is authenticated
  When she requests her capability map without a company identifier
  Then the request is denied with code "company_context_required"
  And the authorization denial is auditable with Ana as the actor

Scenario: Role assignment across companies is denied and audited
  Given Ana may assign roles in company "Vinx Obras"
  And a role belongs to company "Vinx Obras"
  And a membership belongs to company "Vinx Serviços"
  When Ana assigns the role to the membership using company "Vinx Obras"
  Then the command is denied with code "resource_not_found"
  And the authorization denial contains the requested company and correlation identifier
